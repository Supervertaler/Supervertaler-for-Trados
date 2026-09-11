using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Installs the Supervertaler skill for Google Antigravity and the Gemini CLI
    /// (issue #117).
    ///
    /// <para><b>Why this is a button and not a documentation step.</b> Antigravity's
    /// agent has a shell and a file browser, and without a standing instruction it
    /// answers Trados questions by exploring the filesystem instead of calling the
    /// MCP tools - reading <c>projects.xml</c> and the <c>.sdlxliff</c> on disk,
    /// both of which are stale by definition because Studio holds the document in
    /// memory. Measured on one 675-segment project: 8 minutes, 13 shell commands,
    /// zero MCP calls, no answer. The same question with the tool named took 14
    /// seconds. A skill file fixes it completely.</para>
    ///
    /// <para>The delivery was the problem. Asking a translator to create
    /// <c>%%USERPROFILE%%\.gemini\config\skills\supervertaler-trados\SKILL.md</c> by
    /// hand means a dot-folder they have never opened, a file Notepad will happily
    /// save as <c>SKILL.md.txt</c>, and YAML frontmatter that breaks on bad
    /// indentation. Most would not do it, and the ones who skipped it would
    /// conclude the MCP server was slow and unreliable.</para>
    ///
    /// <para>Shipped from the plugin rather than pasted from the docs because the
    /// skill names specific tools and specific rules, and both change as the plugin
    /// gains features - a copy pasted in September is frozen and will gradually
    /// start describing a toolset that no longer matches. Installed this way it
    /// updates when the plugin does.</para>
    /// </summary>
    public static class AntigravitySkillSetup
    {
        private const string LogCategory = "AntigravitySkill";
        private const string ResourceName = "AgentSkill.supervertaler-trados.md";
        private const string SkillFolderName = "supervertaler-trados";

        /// <summary>
        /// <c>%USERPROFILE%\.gemini</c> - shared by Antigravity, Antigravity's IDE
        /// and the Gemini CLI. Not under our own data folder: these apps look here
        /// and nowhere else.
        /// </summary>
        public static string GeminiDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");

        /// <summary>The skills folder Antigravity reads. One folder per skill.</summary>
        public static string SkillsDir => Path.Combine(GeminiDir, "config", "skills");

        public static string SkillDir => Path.Combine(SkillsDir, SkillFolderName);

        /// <summary>The file itself. Must be exactly SKILL.md; the name is the contract.</summary>
        public static string SkillPath => SkillPathIn(GeminiDir);

        /// <summary>
        /// The skill path under an explicit .gemini directory. Test seam:
        /// <see cref="GeminiDir"/> comes from the shell's idea of the user profile,
        /// which cannot be redirected by setting an environment variable, so a test
        /// that wants a sandbox has to be handed one. Same pattern as
        /// <c>BridgeClient.Resolve(runtimeDir)</c>.
        /// </summary>
        internal static string SkillPathIn(string geminiDir) =>
            Path.Combine(geminiDir, "config", "skills", SkillFolderName, "SKILL.md");

        /// <summary>
        /// Whether Antigravity or the Gemini CLI appears to be installed. Only used
        /// to word the dialog - the skill is written regardless, because installing
        /// the app afterwards is a perfectly ordinary order to do things in and
        /// gating on detection would only add a failure mode.
        /// </summary>
        public static bool IsAntigravityInstalled()
        {
            try
            {
                if (Directory.Exists(GeminiDir)) return true;
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return File.Exists(Path.Combine(local, "Programs", "antigravity", "Antigravity.exe"));
            }
            catch { return false; }
        }

        /// <summary>True when a skill file of ours is already there.</summary>
        public static bool IsInstalled() => IsInstalledIn(GeminiDir);

        internal static bool IsInstalledIn(string geminiDir)
        {
            try { return File.Exists(SkillPathIn(geminiDir)); }
            catch { return false; }
        }

        /// <summary>
        /// True when the installed skill differs from the one this plugin ships -
        /// either an older version, or one the user has edited. The dialog uses it
        /// to offer an update rather than silently overwriting, since a user who
        /// customised theirs should not lose it without being asked.
        /// </summary>
        public static bool IsOutdated() => IsOutdatedIn(GeminiDir);

        internal static bool IsOutdatedIn(string geminiDir)
        {
            try
            {
                if (!IsInstalledIn(geminiDir)) return false;
                return !string.Equals(
                    Normalise(File.ReadAllText(SkillPathIn(geminiDir), Encoding.UTF8)),
                    Normalise(ReadShippedSkill()),
                    StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>Line endings vary by who wrote the file last; content is what matters.</summary>
        private static string Normalise(string s) =>
            (s ?? "").Replace("\r\n", "\n").TrimEnd();

        /// <summary>The skill text this plugin build ships.</summary>
        public static string ReadShippedSkill()
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException(
                        "The Antigravity skill is missing from this build (resource '"
                        + ResourceName + "'). Please report this.");
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        public class Result
        {
            public bool Success;
            public string Message;
            /// <summary>Where an existing, differing skill was moved before overwriting.</summary>
            public string BackupPath;
        }

        /// <summary>
        /// Writes the skill, creating the folders. An existing file that differs is
        /// backed up beside itself first - the user may have edited it, and losing
        /// that silently would be worse than the problem this solves.
        /// </summary>
        public static Result Install() => InstallTo(GeminiDir);

        internal static Result InstallTo(string geminiDir)
        {
            try
            {
                var content = ReadShippedSkill();
                var skillPath = SkillPathIn(geminiDir);
                Directory.CreateDirectory(Path.GetDirectoryName(skillPath));

                string backup = null;
                if (File.Exists(skillPath))
                {
                    var existing = File.ReadAllText(skillPath, Encoding.UTF8);
                    if (string.Equals(Normalise(existing), Normalise(content), StringComparison.Ordinal))
                    {
                        return new Result
                        {
                            Success = true,
                            Message = "The Supervertaler skill is already installed and up to date.\r\n\r\n"
                                    + skillPath
                        };
                    }

                    backup = skillPath + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(skillPath, backup, overwrite: true);
                }

                // UTF-8 without a BOM: the frontmatter parser expects the file to
                // begin with "---", and a BOM in front of it is not "---".
                File.WriteAllText(skillPath, content, new UTF8Encoding(false));

                DiagnosticLog.WriteAlways(LogCategory, "Skill written to " + skillPath);

                var msg = new StringBuilder();
                msg.AppendLine("The Supervertaler skill is installed.");
                msg.AppendLine();
                msg.AppendLine(skillPath);
                msg.AppendLine();
                msg.AppendLine("In Antigravity: restart it, then check Settings → Customizations – "
                             + "it appears as “supervertaler-trados”, tagged Global. Start a NEW "
                             + "conversation; one already running keeps the instructions it started with.");
                msg.AppendLine();
                msg.Append("The Gemini CLI reads the same folder, so it is covered too.");

                return new Result { Success = true, Message = msg.ToString(), BackupPath = backup };
            }
            catch (Exception ex)
            {
                try { DiagnosticLog.WriteAlways(LogCategory, "Failed: " + ex.Message); } catch { }
                return new Result
                {
                    Success = false,
                    Message = "Could not write the skill file.\r\n\r\n" + SkillPathIn(geminiDir)
                            + "\r\n\r\n" + ex.Message
                };
            }
        }
    }
}
