using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Embedded-mode web results: a resizable window with one tab per enabled
    /// resource.
    ///
    /// <para><b>One window, reused.</b> A second search re-points the existing
    /// tabs rather than opening another window — the reason embedded mode exists,
    /// since browser mode cannot avoid a new window per search. Tabs are only
    /// rebuilt when the enabled resource set changes.</para>
    ///
    /// <para><b>Tabs load lazily.</b> Only the tab you are looking at gets a
    /// CoreWebView2; the rest stay inert placeholders until selected. Matches the
    /// standalone app, where each source loads when first clicked.</para>
    ///
    /// <para><b>Few browsers live at once, none once the window is closed.</b>
    /// Each loaded tab is a Chromium page with its own renderer processes: one
    /// for the site and one for every other site framed in it (ads, trackers,
    /// consent banners), so an ad-funded dictionary site can cost half a dozen.
    /// Keeping every tab ever opened, and keeping them all while the window was
    /// merely hidden, once left 63 msedgewebview2.exe processes and 5.9 GB under
    /// Studio after a few searches across nine resources. Now only the
    /// <see cref="MaxLiveViews"/> most recently shown tabs keep a browser, and
    /// closing the window releases them all, so the WebView2 processes exit.
    /// What carries over is the profile on disk: persistent cookies (sign-ins
    /// that "remember me"), cache, local storage.</para>
    /// </summary>
    public class WebSearchBrowserForm : Form
    {
        /// <summary>
        /// Tabs that keep a live browser: the selected one and the two shown
        /// before it. Flicking between three sources stays instant; going back
        /// to a fourth reloads it (a fraction of a second once the browser is
        /// running). The cap holds however many resources are enabled: with 9,
        /// or all 43, at most three tabs' worth of renderers run at once.
        /// </summary>
        private const int MaxLiveViews = 3;

        /// <summary>
        /// Detects a bot-check interstitial. Same probe as the standalone
        /// SuperLookup app: these walls render as ordinary pages, so the text is
        /// the only reliable signal, and they often arrive with HTTP 403 — which
        /// is why a failed navigation is not by itself the test.
        /// </summary>
        private const string CloudflareProbe =
            "(function(){try{var b=document.body?document.body.innerText:'';" +
            "return /just a moment|checking your browser|verify you are human|" +
            "uses a security service to protect|enable javascript and cookies to continue/i" +
            ".test(b);}catch(e){return false;}})()";

        private sealed class TabState
        {
            public WebSearchTarget Target;
            public WebView2 View;          // null until selected, and again once released
            public Task ViewReady;         // View's start-up; see ReleaseView
            public long LastShown;         // selection order, for MaxLiveViews
            public bool NeedsNavigate = true;
            public Panel Banner;           // the bot-check hand-off offer, if shown
            public bool RaiseWhenLoaded;   // re-assert the window once this load finishes
            public readonly HashSet<string> BouncedUrls =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly TabControl _tabs;
        private readonly Label _status;
        private readonly Button _btnOpenInBrowser;

        private CoreWebView2Environment _environment;
        private List<string> _layoutIds = new List<string>();
        private bool _ownerSet;
        private long _selections;   // stamps TabState.LastShown

        public WebSearchBrowserForm()
        {
            Text = "SuperSearch";
            Size = new Size(1200, 850);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(600, 400);
            try { Icon = IconHelper.AppIcon; } catch { /* icon is cosmetic */ }

            // Outlined and marked, in the Assistant panel's tab font: at the
            // default font the tabs ran together and the selected one was hard
            // to find. Any larger and nine tabs no longer fit the default
            // window at 125%.
            _tabs = new OutlinedTabControl
            {
                Dock = DockStyle.Fill,
                MarkSelected = true,
                Font = new Font("Segoe UI", UiScale.FontSize(8.5f)),
                Padding = new Point(UiScale.Pixels(7), UiScale.Pixels(4))
            };
            _tabs.SelectedIndexChanged += async (s, e) => await ActivateSelectedTabAsync();
            Controls.Add(_tabs);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 28 };

            _btnOpenInBrowser = new Button
            {
                Text = "Open this tab in my browser",
                Dock = DockStyle.Right,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            // Measured rather than guessed — a fixed 190px clipped the caption to
            // "Open this tab in my" at the default font, and would clip worse at
            // higher DPI.
            _btnOpenInBrowser.Width =
                TextRenderer.MeasureText(_btnOpenInBrowser.Text, _btnOpenInBrowser.Font).Width + 24;
            _btnOpenInBrowser.Click += (s, e) => OpenCurrentTabInBrowser();
            bottom.Controls.Add(_btnOpenInBrowser);

            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                ForeColor = Color.FromArgb(110, 110, 110)
            };
            bottom.Controls.Add(_status);
            _status.BringToFront();

            Controls.Add(bottom);
        }

        /// <summary>
        /// Points the window at a new search. Returns false only if the WebView2
        /// environment could not be created, in which case the caller falls back
        /// to the user's browser.
        /// </summary>
        public async Task<bool> ShowResultsAsync(string query, IList<WebSearchTarget> targets)
        {
            if (_environment == null)
            {
                _environment = await WebView2Support.CreateEnvironmentAsync();
                if (_environment == null) return false;

                // The environment is kept for the session (it holds no process);
                // the browser processes come and go with the views. This line
                // is the log's proof that closing the window really ended them.
                _environment.BrowserProcessExited += (s, e) =>
                    DiagnosticLog.Log("WebView2",
                        $"Browser processes exited ({e.BrowserProcessExitKind}, pid {e.BrowserProcessId})");
            }

            Text = $"SuperSearch – {query}";

            var ids = targets.Select(t => t.Resource.Id).ToList();
            if (!ids.SequenceEqual(_layoutIds, StringComparer.OrdinalIgnoreCase))
            {
                RebuildTabs(targets);
                _layoutIds = ids;
            }
            else
            {
                // Same resources, new query: keep the loaded views but mark every
                // tab stale. Only the visible one re-navigates now; the others do
                // it when selected, so an unopened tab never costs anything.
                for (int i = 0; i < _tabs.TabPages.Count && i < targets.Count; i++)
                {
                    var state = (TabState)_tabs.TabPages[i].Tag;
                    state.Target = targets[i];
                    state.NeedsNavigate = true;
                    state.BouncedUrls.Clear();
                }
            }

            if (_tabs.TabPages.Count > 0) _tabs.SelectedIndex = 0;
            await ActivateSelectedTabAsync();

            _status.Text = $"{targets.Count} resource(s) · “{query}” · WebView2 {WebView2Support.RuntimeVersion}";
            return true;
        }

        private void RebuildTabs(IList<WebSearchTarget> targets)
        {
            // Release the browsers through ReleaseView, which knows how to end one
            // that is still starting, then dispose the pages themselves: clearing
            // TabPages only detaches them. (The loop this replaces disposed each
            // page's controls while enumerating them, which skips every other one.)
            var old = _tabs.TabPages.Cast<TabPage>().ToList();
            foreach (var page in old) ReleaseView(page.Tag as TabState, showPlaceholder: false);
            _tabs.TabPages.Clear();
            foreach (var page in old) page.Dispose();

            foreach (var target in targets)
            {
                var page = new TabPage(target.Resource.Name)
                {
                    UseVisualStyleBackColor = true,
                    Tag = new TabState { Target = target }
                };
                page.Controls.Add(NewPlaceholder());
                _tabs.TabPages.Add(page);
            }
        }

        private static Label NewPlaceholder() => new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(140, 140, 140),
            Text = "Loading…"
        };

        /// <summary>
        /// Ends a tab's browser, so its renderer processes exit, and leaves the
        /// tab as if it had never been opened: selecting it again loads the
        /// search afresh. Safe to call on a tab without one.
        ///
        /// <para>A view still starting up is not disposed here but when its
        /// start-up settles. The WinForms WebView2 control's Dispose closes its
        /// browser only once initialisation has finished; disposed mid-start,
        /// it relies on the runtime noticing its window go and aborting the
        /// start. Runtime 156 does (E_ABORT, tested), but waiting for
        /// <see cref="TabState.ViewReady"/> doesn't depend on it, and a leaked
        /// browser here would be invisible until Studio closed.</para>
        /// </summary>
        private void ReleaseView(TabState state, bool showPlaceholder = true)
        {
            var view = state?.View;
            if (view == null) return;

            var ready = state.ViewReady;
            state.View = null;
            state.ViewReady = null;
            state.NeedsNavigate = true;
            state.RaiseWhenLoaded = false;
            state.BouncedUrls.Clear();     // a reload may offer the hand-off again
            RemoveBanner(state);

            var page = view.Parent as TabPage;
            if (page != null)
            {
                page.Controls.Remove(view);
                if (showPlaceholder && page.Controls.Count == 0) page.Controls.Add(NewPlaceholder());
            }

            if (ready == null || ready.IsCompleted)
            {
                DisposeQuietly(view);
            }
            else
            {
                ready.ContinueWith(_ => DisposeQuietly(view), System.Threading.CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        private static void DisposeQuietly(WebView2 view)
        {
            try { view.Dispose(); }
            catch (Exception ex)
            {
                DiagnosticLog.Log("WebView2", $"Releasing a view failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Releases every tab's browser; the window keeps its tabs.</summary>
        private void ReleaseAllViews()
        {
            int released = 0;
            foreach (TabPage page in _tabs.TabPages)
            {
                var state = page.Tag as TabState;
                if (state?.View == null) continue;
                ReleaseView(state);
                released++;
            }
            if (released > 0)
                DiagnosticLog.Log("WebView2", $"Web window closed: released {released} browser view(s)");
        }

        /// <summary>
        /// Keeps <paramref name="current"/> and the most recently shown others,
        /// <see cref="MaxLiveViews"/> in all, and releases the rest. Runs before
        /// a new view is created, so the cap is never exceeded even for a moment.
        /// </summary>
        private void ReleaseLeastRecentViews(TabState current)
        {
            var surplus = _tabs.TabPages.Cast<TabPage>()
                .Select(p => p.Tag as TabState)
                .Where(s => s?.View != null && s != current)
                .OrderByDescending(s => s.LastShown)
                .Skip(MaxLiveViews - 1)
                .ToList();
            foreach (var state in surplus) ReleaseView(state);
        }

        /// <summary>
        /// Gives the selected tab a browser if it has not got one, then navigates
        /// it if its URL is stale. This is the whole of the lazy-loading policy.
        /// </summary>
        private async Task ActivateSelectedTabAsync()
        {
            // Never while hidden: a window closed while a search was starting must
            // not start a browser behind the user's back.
            if (!Visible) return;
            var page = _tabs.SelectedTab;
            if (page == null || _environment == null) return;
            var state = page.Tag as TabState;
            if (state == null) return;

            state.LastShown = ++_selections;
            WebView2 view = state.View;
            try
            {
                if (view == null)
                {
                    ReleaseLeastRecentViews(state);

                    view = new WebView2 { Dock = DockStyle.Fill };
                    foreach (var old in page.Controls.Cast<Control>().ToList())
                        old.Dispose();              // the "Loading…" placeholder, or an old error
                    page.Controls.Add(view);
                    state.View = view;
                    view.NavigationCompleted += async (s, e) => await OnNavigationCompletedAsync(state, view);

                    // Every view shares one environment, so they share a cookie
                    // jar: signing in on one tab carries to the rest.
                    state.ViewReady = view.EnsureCoreWebView2Async(_environment);
                }

                // Also reached when the user comes back to a tab whose browser is
                // still starting: it must wait too, not navigate a null CoreWebView2.
                await state.ViewReady;

                // Released while it was starting (window closed, or flicked
                // past): ReleaseView has already arranged its disposal.
                if (state.View != view) return;

                if (state.NeedsNavigate)
                {
                    state.NeedsNavigate = false;
                    state.RaiseWhenLoaded = true;
                    RemoveBanner(state);   // a stale wall notice must not outlive its page
                    view.CoreWebView2.Navigate(state.Target.Url);
                }
            }
            catch (Exception ex)
            {
                if (state.View != view) return;   // released meanwhile: not a failure

                DiagnosticLog.Log("WebView2",
                    $"Tab '{state.Target.Resource.Name}' failed: {ex.GetType().Name}: {ex.Message}");
                // Release first: the error page used to replace a view that was
                // never disposed. Coming back to the tab now retries.
                ReleaseView(state, showPlaceholder: false);
                ShowTabError(page, state.Target, ex);
            }
        }

        /// <summary>
        /// After each load, check whether we landed on a bot-check wall and, if
        /// so, hand the page to the real browser — where the user is signed in
        /// and passes instantly. Only tabs the user actually opened can reach
        /// here, so this never launches a browser for something unseen.
        /// </summary>
        private async Task OnNavigationCompletedAsync(TabState state, WebView2 view)
        {
            try
            {
                // A released view can still deliver one last event.
                if (state.View != view) return;
                var core = view.CoreWebView2;
                if (core == null) return;

                // The load is only NOW finished. Navigate() returned the moment
                // the request was queued, so the raise that followed it happened
                // while the WebView2 browser process was still starting — and that
                // process takes the foreground as it goes. Re-asserting here is
                // the first point at which nothing is left to steal it back.
                if (state.RaiseWhenLoaded)
                {
                    state.RaiseWhenLoaded = false;
                    BringToFrontHard();
                }

                var url = core.Source;
                if (string.IsNullOrEmpty(url) || state.BouncedUrls.Contains(url)) return;

                var result = await core.ExecuteScriptAsync(CloudflareProbe);
                if (state.View != view) return;   // released while the probe ran
                if (!string.Equals(result, "true", StringComparison.OrdinalIgnoreCase)) return;

                state.BouncedUrls.Add(url);
                DiagnosticLog.Log("WebView2",
                    $"Bot-check wall on {state.Target.Resource.Name}; offering a hand-off");

                ShowWallBanner(state, url);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("WebView2", $"Bot-check probe failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Offers a hand-off to the real browser when a bot-check wall is
        /// detected, rather than performing it.
        ///
        /// <para>The standalone app jumps straight to the browser, which works
        /// there because it <i>is</i> the foreground app. Inside Trados the same
        /// behaviour yanks you out of the editor mid-translation and leaves the
        /// auto-hidden pane collapsed behind you — so here the wall is only
        /// flagged, and the click is the user's.</para>
        ///
        /// <para>The wall page is left visible underneath: some of them resolve
        /// themselves after a few seconds, and replacing the page would throw
        /// that away.</para>
        /// </summary>
        private void ShowWallBanner(TabState state, string url)
        {
            var page = state.View?.Parent as TabPage;
            if (page == null || state.Banner != null) return;

            var banner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = Color.FromArgb(255, 243, 208),   // the non-translatable chip yellow
                Padding = new Padding(10, 0, 10, 0)
            };

            var open = new Button
            {
                Text = "Open in my browser",
                Dock = DockStyle.Right,
                Width = 160,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                BackColor = Color.White
            };
            open.Click += (s, e) =>
            {
                WebSearchLauncher.OpenOne(url);
                RemoveBanner(state);
            };
            banner.Controls.Add(open);

            var dismiss = new Button
            {
                Text = "Dismiss",
                Dock = DockStyle.Right,
                Width = 80,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                BackColor = Color.White
            };
            dismiss.Click += (s, e) => RemoveBanner(state);
            banner.Controls.Add(dismiss);

            string host;
            try { host = new Uri(url).Host; } catch { host = state.Target.Resource.Name; }

            banner.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(60, 50, 20),
                Text = $"{host} is showing a human-verification check. "
                     + "Your own browser will usually pass it instantly."
            });

            state.Banner = banner;
            page.Controls.Add(banner);
            banner.BringToFront();
        }

        private void RemoveBanner(TabState state)
        {
            if (state?.Banner == null) return;
            try
            {
                state.Banner.Parent?.Controls.Remove(state.Banner);
                state.Banner.Dispose();
            }
            catch { /* the tab may already have been rebuilt */ }
            state.Banner = null;
        }

        private void OpenCurrentTabInBrowser()
        {
            var state = _tabs.SelectedTab?.Tag as TabState;
            if (state == null) return;

            // Prefer where the tab actually ended up — the user may have clicked
            // through several pages — and fall back to the search URL if the tab
            // was never loaded.
            var url = state.View?.CoreWebView2?.Source;
            if (string.IsNullOrEmpty(url) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                url = state.Target?.Url;

            WebSearchLauncher.OpenOne(url);
        }

        private static void ShowTabError(TabPage page, WebSearchTarget target, Exception ex)
        {
            foreach (var old in page.Controls.Cast<Control>().ToList()) old.Dispose();
            page.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = $"{target.Resource.Name} could not be loaded.\n\n{ex.Message}\n\n"
                     + "Use \"Open this tab in my browser\" below."
            });
        }

        /// <summary>
        /// Brings the window up and in front, restoring it first if minimised.
        /// </summary>
        public void BringToFrontHard()
        {
            try
            {
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                Show();
                EnsureOwnedByHost();
                Activate();
                ForegroundWindow.Force(Handle);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("WebView2", $"Could not raise the window: {ex.Message}");
            }
        }

        /// <summary>
        /// Makes Trados the owner of this window, once, as soon as it has a handle.
        ///
        /// <para>Forcing the foreground alone was not enough, and could not be:
        /// <c>Navigate()</c> returns as soon as the request is queued, so the page
        /// is still loading when we raise the window, and the WebView2 browser
        /// process takes the foreground whenever it finishes — after us. Rather
        /// than trying to win that race, ownership removes it: the window manager
        /// keeps an owned window above its owner regardless of who has focus.</para>
        /// </summary>
        private void EnsureOwnedByHost()
        {
            // Retried until it takes, rather than attempted once: the host handle
            // may not have been available the first time round, and Hide()/Show()
            // cycles are cheap to re-assert against.
            if (_ownerSet || !IsHandleCreated) return;
            var host = ForegroundWindow.HostMainWindow();
            if (host == IntPtr.Zero) return;
            _ownerSet = ForegroundWindow.SetOwner(Handle, host);
            DiagnosticLog.Log("Foreground",
                _ownerSet
                    ? $"Web window 0x{Handle.ToInt64():X} now owned by host 0x{host.ToInt64():X}"
                    : $"Failed to set owner for web window 0x{Handle.ToInt64():X}");
        }

        /// <summary>
        /// Closing hides the window rather than disposing it, so the next search
        /// reuses its tabs, size and position. The browsers do not survive it:
        /// see <see cref="Dismiss"/>.
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Dismiss();
                return;
            }
            base.OnFormClosing(e);
        }

        /// <summary>
        /// Puts the window away and releases every tab's browser, so the WebView2
        /// processes exit within a few seconds; the next search starts them again
        /// in about a second. Hiding used to keep them all until Studio closed.
        ///
        /// <para>Deliberately not done on every hide: Windows also hides this
        /// window while Studio, its owner, is minimised, and brings it back on
        /// restore with nothing to reload the tab it shows.</para>
        /// </summary>
        public void Dismiss()
        {
            Hide();
            ReleaseAllViews();
        }
    }
}
