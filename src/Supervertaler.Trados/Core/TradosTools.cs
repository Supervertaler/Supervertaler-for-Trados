using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Trados Studio query tools that the AI Assistant can invoke via tool use.
    /// These read local Trados data (projects.xml, TM metadata, etc.) and return
    /// JSON results that the LLM can use to answer user questions.
    /// </summary>
    public static class TradosTools
    {
        // ─── Tool Definitions ────────────────────────────────────────

        /// <summary>
        /// Returns tool definitions JSON formatted for the given AI provider.
        /// Claude uses "input_schema"; OpenAI wraps in {type:"function",function:{...}} with "parameters";
        /// Gemini wraps in [{functionDeclarations:[...]}] with "parameters".
        /// </summary>
        public static string GetToolDefinitionsJson(string provider)
        {
            var core = GetCoreToolDefinitions();

            if (provider == "claude")
            {
                // Claude: uses "input_schema" instead of "parameters"
                return core.Replace("\"parameters\":", "\"input_schema\":");
            }

            if (provider == "gemini")
            {
                // Gemini: wrap all tools in a single functionDeclarations object
                return "[{\"functionDeclarations\":" + core + "}]";
            }

            // OpenAI-compatible (OpenAI, Grok, Mistral, OpenRouter, Custom):
            // wrap each tool in {"type":"function","function":{...}}
            return WrapAsOpenAiTools(core);
        }

        /// <summary>
        /// Returns the core tool definitions array using "parameters" as the schema key.
        /// This is the canonical format – provider-specific methods transform it.
        /// </summary>
        private static string GetCoreToolDefinitions()
        {
            return @"[
  {
    ""name"": ""studio_list_projects"",
    ""description"": ""Lists all projects registered in Trados Studio with their name, status, creation date, and file path. Use this when the user asks about their projects, project status, or wants an overview of their work."",
    ""parameters"": {
      ""type"": ""object"",
      ""properties"": {
        ""status_filter"": {
          ""type"": ""string"",
          ""description"": ""Optional filter: 'InProgress', 'Completed', or 'Archived'. Omit to list all."",
          ""enum"": [""InProgress"", ""Completed"", ""Archived""]
        }
      },
      ""required"": []
    }
  },
  {
    ""name"": ""studio_get_project"",
    ""description"": ""Gets detailed information about a specific Trados Studio project by name, including source/target languages, files, and status. Use when the user asks about a specific project."",
    ""parameters"": {
      ""type"": ""object"",
      ""properties"": {
        ""project_name"": {
          ""type"": ""string"",
          ""description"": ""The name (or partial name) of the project to look up.""
        }
      },
      ""required"": [""project_name""]
    }
  },
  {
    ""name"": ""studio_get_project_statistics"",
    ""description"": ""Gets word count and analysis statistics for a Trados Studio project, broken down by match category (perfect, context, exact, fuzzy, new, repetitions). Use when the user asks about project statistics, word counts, analysis results, or how much work remains."",
    ""parameters"": {
      ""type"": ""object"",
      ""properties"": {
        ""project_name"": {
          ""type"": ""string"",
          ""description"": ""The name (or partial name) of the project.""
        }
      },
      ""required"": [""project_name""]
    }
  },
  {
    ""name"": ""studio_get_file_status"",
    ""description"": ""Gets the confirmation/translation status of all files in a Trados Studio project, showing how many segments and words are in each status (not started, draft, translated, approved, signed off). Use when the user asks about file progress, translation status, or completion."",
    ""parameters"": {
      ""type"": ""object"",
      ""properties"": {
        ""project_name"": {
          ""type"": ""string"",
          ""description"": ""The name (or partial name) of the project.""
        }
      },
      ""required"": [""project_name""]
    }
  },
  {
    ""name"": ""studio_list_project_termbases"",
    ""description"": ""Lists termbases attached to a specific Trados Studio project, with their file paths and language index mappings. Use when the user asks about terminology resources in a project."",
    ""parameters"": {
      ""type"": ""object"",
      ""properties"": {
        ""project_name"": {
          ""type"": ""string"",
          ""description"": ""The name (or partial name) of the project.""
        }
      },
      ""required"": [""project_name""]
    }
  },
  {
    ""name"": ""studio_get_tm_info"",
    ""description"": ""Gets detailed information about a specific translation memory (.sdltm file), including language pair, segment count, creation date, and description. Use when the user asks about a specific TM's details."",
    ""parameters"": {
      ""type"": ""object"",
      ""properties"": {
        ""tm_name"": {
          ""type"": ""string"",
          ""description"": ""The name (or partial name) of the TM to look up.""
        }
      },
      ""required"": [""tm_name""]
    }
  },
  {
    ""name"": ""studio_search_tm"",
    ""description"": ""Searches a translation memory for segments containing specific text in the source or target. Returns matching translation units with source, target, and usage info. Use when the user wants to find how something was translated before, or check TM content."",
    ""parameters"": {
      ""type"": ""object"",
      ""properties"": {
        ""tm_name"": {
          ""type"": ""string"",
          ""description"": ""The name (or partial name) of the TM to search.""
        },
        ""search_text"": {
          ""type"": ""string"",
          ""description"": ""The text to search for in source or target segments.""
        },
        ""max_results"": {
          ""type"": ""integer"",
          ""description"": ""Maximum results to return (default 20, max 50).""
        }
      },
      ""required"": [""tm_name"", ""search_text""]
    }
  },
  {
    ""name"": ""studio_list_tms"",
    ""description"": ""Lists all translation memories (TMs) found in the Trados Studio TM folder. Use when the user asks about their translation memories or TM setup."",
    ""parameters"": {
      ""type"": ""object"",
      ""properties"": {},
      ""required"": []
    }
  },
  {
    ""name"": ""studio_list_project_templates"",
    ""description"": ""Lists all project templates available in Trados Studio. Use when the user asks about their templates or wants to know which templates are available."",
    ""parameters"": {
      ""type"": ""object"",
      ""properties"": {},
      ""required"": []
    }
  }
]";
        }

        /// <summary>
        /// Wraps core tool definitions into OpenAI's {type:"function",function:{...}} format.
        /// </summary>
        private static string WrapAsOpenAiTools(string coreJson)
        {
            var sb = new StringBuilder("[");
            int depth = 0;
            int objStart = -1;
            bool inString = false;
            bool escaped = false;
            bool first = true;

            for (int i = 0; i < coreJson.Length; i++)
            {
                var c = coreJson[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;

                if (c == '{')
                {
                    if (depth == 0) objStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("{\"type\":\"function\",\"function\":");
                        sb.Append(coreJson, objStart, i - objStart + 1);
                        sb.Append("}");
                        objStart = -1;
                    }
                }
            }

            sb.Append("]");
            return sb.ToString();
        }

        // ─── Tool Dispatch ──────────────────────────────────────────

        /// <summary>
        /// Executes a tool by name with the given JSON input arguments.
        /// Returns a JSON string with the result.
        /// </summary>
        public static string ExecuteTool(string toolName, string inputJson)
        {
            try
            {
                switch (toolName)
                {
                    case "studio_list_projects":
                        return ListProjects(ExtractJsonField(inputJson, "status_filter"));
                    case "studio_get_project":
                        return GetProject(ExtractJsonField(inputJson, "project_name"));
                    case "studio_get_project_statistics":
                        return GetProjectStatistics(ExtractJsonField(inputJson, "project_name"));
                    case "studio_get_file_status":
                        return GetFileStatus(ExtractJsonField(inputJson, "project_name"));
                    case "studio_list_project_termbases":
                        return ListProjectTermbases(ExtractJsonField(inputJson, "project_name"));
                    case "studio_get_tm_info":
                        return GetTmInfo(ExtractJsonField(inputJson, "tm_name"));
                    case "studio_search_tm":
                        return SearchTm(
                            ExtractJsonField(inputJson, "tm_name"),
                            ExtractJsonField(inputJson, "search_text"),
                            ExtractJsonInt(inputJson, "max_results"));
                    case "studio_list_tms":
                        return ListTranslationMemories();
                    case "studio_list_project_templates":
                        return ListProjectTemplates();
                    default:
                        return JsonError($"Unknown tool: {toolName}");
                }
            }
            catch (Exception ex)
            {
                return JsonError($"Tool execution error: {ex.Message}");
            }
        }

        // ─── Tool Implementations ───────────────────────────────────

        private static string ListProjects(string statusFilter)
        {
            var xmlPaths = GetProjectsXmlPaths();
            if (xmlPaths.Count == 0)
                return JsonError("Could not find any Trados Studio projects.xml. Is Trados Studio installed?");

            var sb = new StringBuilder();
            sb.Append("{\"projects\":[");
            int count = 0;
            // The same project can be registered under more than one Studio
            // version – dedupe on the resolved .sdlproj path, newest Studio wins.
            var seenProjPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var xmlPath in xmlPaths)
            {
                XDocument doc;
                try { doc = XDocument.Load(xmlPath); }
                catch { continue; }
                var studio = StudioLabelFromXmlPath(xmlPath);

                foreach (var item in doc.Descendants("ProjectListItem"))
                {
                    var info = item.Element("ProjectInfo");
                    if (info == null) continue;

                    var name = info.Attribute("Name")?.Value ?? "";
                    var status = info.Attribute("Status")?.Value ?? "";
                    var createdAt = info.Attribute("CreatedAt")?.Value ?? "";
                    var projectFilePath = item.Attribute("ProjectFilePath")?.Value ?? "";

                    // Apply status filter if specified
                    if (!string.IsNullOrEmpty(statusFilter) &&
                        !status.Equals(statusFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var projPath = ResolveProjectPath(projectFilePath, xmlPath);
                    if (!string.IsNullOrEmpty(projPath) && !seenProjPaths.Add(projPath))
                        continue; // already listed from a newer Studio's registry

                    var friendlyStatus = MapStatus(status);
                    var dateStr = FormatDate(createdAt);

                    if (count > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append("\"name\":").Append(JsonStr(name));
                    sb.Append(",\"status\":").Append(JsonStr(friendlyStatus));
                    sb.Append(",\"created\":").Append(JsonStr(dateStr));
                    if (!string.IsNullOrEmpty(studio))
                        sb.Append(",\"studio\":").Append(JsonStr(studio));
                    if (!string.IsNullOrEmpty(projPath))
                        sb.Append(",\"path\":").Append(JsonStr(Path.GetDirectoryName(projPath) ?? ""));
                    sb.Append("}");
                    count++;
                }
            }

            sb.Append("],\"total\":").Append(count).Append("}");
            return sb.ToString();
        }

        private static string GetProject(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return JsonError("Project name is required.");

            var xmlPaths = GetProjectsXmlPaths();
            if (xmlPaths.Count == 0)
                return JsonError("Could not find any Trados Studio projects.xml.");

            // Search every Studio version's registry (newest first) – projects
            // live in per-version projects.xml files.
            var searchLower = projectName.ToLowerInvariant();
            XElement match = null;
            string xmlPath = null;
            foreach (var candidate in xmlPaths)
            {
                XDocument doc;
                try { doc = XDocument.Load(candidate); }
                catch { continue; }
                match = doc.Descendants("ProjectListItem").FirstOrDefault(i =>
                {
                    var name = i.Element("ProjectInfo")?.Attribute("Name")?.Value;
                    return name != null && name.ToLowerInvariant().Contains(searchLower);
                });
                if (match != null) { xmlPath = candidate; break; }
            }

            if (match == null)
                return JsonError($"No project found matching '{projectName}'.");

            var info = match.Element("ProjectInfo");
            var name2 = info?.Attribute("Name")?.Value ?? "";
            var status = info?.Attribute("Status")?.Value ?? "";
            var createdAt = info?.Attribute("CreatedAt")?.Value ?? "";
            var projectFilePath = match.Attribute("ProjectFilePath")?.Value ?? "";

            // Try to read the .sdlproj file for more details
            var projPath = ResolveProjectPath(projectFilePath, xmlPath);
            string sourceLang = null;
            var targetLangs = new List<string>();
            var files = new List<string>();

            if (projPath != null && File.Exists(projPath))
            {
                try
                {
                    var projDoc = XDocument.Load(projPath);
                    var ns = projDoc.Root?.GetDefaultNamespace();

                    // Source language
                    var slElem = projDoc.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "SourceLanguageCode");
                    sourceLang = slElem?.Value;

                    // Target languages
                    var tlElems = projDoc.Descendants()
                        .Where(e => e.Name.LocalName == "TargetLanguageCode");
                    foreach (var tl in tlElems)
                    {
                        if (!string.IsNullOrEmpty(tl.Value) && !targetLangs.Contains(tl.Value))
                            targetLangs.Add(tl.Value);
                    }

                    // Language directions (fallback)
                    if (targetLangs.Count == 0)
                    {
                        var langDirs = projDoc.Descendants()
                            .Where(e => e.Name.LocalName == "LanguageDirection");
                        foreach (var ld in langDirs)
                        {
                            var tc = ld.Attribute("TargetLanguageCode")?.Value;
                            if (!string.IsNullOrEmpty(tc) && !targetLangs.Contains(tc))
                                targetLangs.Add(tc);
                            if (sourceLang == null)
                                sourceLang = ld.Attribute("SourceLanguageCode")?.Value;
                        }
                    }

                    // Project files
                    var fileElems = projDoc.Descendants()
                        .Where(e => e.Name.LocalName == "FileVersion" || e.Name.LocalName == "LanguageFile");
                    foreach (var f in fileElems)
                    {
                        var fname = f.Attribute("FileName")?.Value ?? f.Attribute("Name")?.Value;
                        if (!string.IsNullOrEmpty(fname) && !files.Contains(fname)
                            && !fname.EndsWith(".sdlproj", StringComparison.OrdinalIgnoreCase))
                            files.Add(fname);
                    }
                }
                catch { /* Silently skip parse errors */ }
            }

            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"name\":").Append(JsonStr(name2));
            sb.Append(",\"status\":").Append(JsonStr(MapStatus(status)));
            sb.Append(",\"created\":").Append(JsonStr(FormatDate(createdAt)));
            if (sourceLang != null)
                sb.Append(",\"sourceLanguage\":").Append(JsonStr(sourceLang));
            if (targetLangs.Count > 0)
            {
                sb.Append(",\"targetLanguages\":[");
                for (int i = 0; i < targetLangs.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(JsonStr(targetLangs[i]));
                }
                sb.Append("]");
            }
            if (files.Count > 0)
            {
                sb.Append(",\"files\":[");
                for (int i = 0; i < files.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(JsonStr(files[i]));
                }
                sb.Append("]");
            }
            var folder = Path.GetDirectoryName(projPath);
            if (!string.IsNullOrEmpty(folder))
                sb.Append(",\"path\":").Append(JsonStr(folder));
            sb.Append("}");
            return sb.ToString();
        }

        private static string ListTranslationMemories()
        {
            // Primary location: Documents\<Studio folder>\Translation Memories\
            var tmFolders = GetStudioDocumentFolders()
                .Select(f => Path.Combine(f, "Translation Memories"))
                .ToArray();

            var tmFiles = new List<string>();
            foreach (var folder in tmFolders)
            {
                if (Directory.Exists(folder))
                {
                    try { tmFiles.AddRange(Directory.GetFiles(folder, "*.sdltm", SearchOption.AllDirectories)); }
                    catch { }
                }
            }

            // Scan all .sdlproj files for TM references and project TM folders –
            // across every Studio version's registry.
            var projectDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var xmlPath in GetProjectsXmlPaths())
            {
                XDocument doc;
                try { doc = XDocument.Load(xmlPath); }
                catch { continue; }

                foreach (var item in doc.Descendants("ProjectListItem"))
                {
                    var pfp = item.Attribute("ProjectFilePath")?.Value;
                    var projPath = ResolveProjectPath(pfp, xmlPath);
                    if (projPath == null || !File.Exists(projPath)) continue;

                    var projDir = Path.GetDirectoryName(projPath);
                    if (projDir == null) continue;

                    // Scan .sdlproj for TM URIs (sdltm.file:/// prefix)
                    try
                    {
                        var projDoc = XDocument.Load(projPath);
                        var tmUris = projDoc.Descendants()
                            .Where(e => e.Name.LocalName == "MainTranslationProviderItem"
                                     || e.Name.LocalName == "ProjectTranslationProviderItem")
                            .Select(e => e.Attribute("Uri")?.Value)
                            .Where(u => u != null && u.Contains(".sdltm"))
                            .Distinct();

                        foreach (var uri in tmUris)
                        {
                            var path = ResolveTmUri(uri, projDir);
                            if (path != null && File.Exists(path))
                                tmFiles.Add(path);
                        }
                    }
                    catch { }

                    // Also scan project's Tm subfolder for any .sdltm files
                    if (!projectDirs.Contains(projDir))
                    {
                        projectDirs.Add(projDir);
                        var tmSubDir = Path.Combine(projDir, "Tm");
                        if (Directory.Exists(tmSubDir))
                        {
                            try { tmFiles.AddRange(Directory.GetFiles(tmSubDir, "*.sdltm", SearchOption.AllDirectories)); }
                            catch { }
                        }
                    }
                }
            }

            // Deduplicate by full path
            tmFiles = tmFiles
                .Select(f => { try { return Path.GetFullPath(f); } catch { return f; } })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sb = new StringBuilder();
            sb.Append("{\"translationMemories\":[");
            for (int i = 0; i < tmFiles.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var name = Path.GetFileNameWithoutExtension(tmFiles[i]);
                sb.Append("{\"name\":").Append(JsonStr(name));
                sb.Append(",\"path\":").Append(JsonStr(tmFiles[i]));
                sb.Append("}");
            }
            sb.Append("],\"total\":").Append(tmFiles.Count).Append("}");
            return sb.ToString();
        }

        private static string ListProjectTemplates()
        {
            var templateFolders = GetStudioDocumentFolders()
                .Select(f => Path.Combine(f, "Project Templates"))
                .ToArray();

            var templates = new List<string>();
            foreach (var folder in templateFolders)
            {
                if (Directory.Exists(folder))
                {
                    templates.AddRange(Directory.GetFiles(folder, "*.sdltpl", SearchOption.AllDirectories));
                }
            }

            var sb = new StringBuilder();
            sb.Append("{\"projectTemplates\":[");
            for (int i = 0; i < templates.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var name = Path.GetFileNameWithoutExtension(templates[i]);
                sb.Append("{\"name\":").Append(JsonStr(name));
                sb.Append(",\"path\":").Append(JsonStr(templates[i]));
                sb.Append("}");
            }
            sb.Append("],\"total\":").Append(templates.Count).Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Reads analysis statistics directly from a specific .sdlproj file,
        /// bypassing the projects.xml name lookup. The bridge uses this for the
        /// project open in the editor, so recently-created or otherwise
        /// unregistered projects work (and it can't resolve the wrong copy).
        /// </summary>
        public static string GetProjectStatisticsByFile(string sdlprojPath, string displayName)
        {
            if (string.IsNullOrEmpty(sdlprojPath) || !File.Exists(sdlprojPath))
                return JsonError("Project file not found on disk: " + (sdlprojPath ?? "(null)"));
            XDocument doc;
            try { doc = XDocument.Load(sdlprojPath); }
            catch (Exception ex) { return JsonError("Could not read project file: " + ex.Message); }

            // Prefer the analysis REPORT (Reports\Analyze Files*.xml) – it holds the
            // real leverage bands and is written by any Analyse run. The inline
            // AnalysisStatistics in the .sdlproj is frequently empty ("None") after
            // an SDK-run analysis, which is why bands came back as zero.
            var fromReport = TryBuildStatsFromAnalyzeReport(sdlprojPath, displayName, doc);
            if (fromReport != null) return fromReport;

            return BuildProjectStatisticsJson(doc, displayName);
        }

        private static string GetProjectStatistics(string projectName)
        {
            var projPath = FindProjectFile(projectName);
            if (projPath == null) return JsonError($"No project found matching '{projectName}'.");

            return GetProjectStatisticsByFile(projPath, projectName);
        }

        /// <summary>
        /// Builds the analysis-band JSON from the newest "Analyze Files*.xml" report
        /// in the project's Reports folder (the authoritative source of leverage
        /// figures). Returns null when there is no report to read.
        /// </summary>
        private static string TryBuildStatsFromAnalyzeReport(string sdlprojPath, string displayName, XDocument projDoc)
        {
            try
            {
                var projDir = Path.GetDirectoryName(sdlprojPath);
                if (projDir == null) return null;
                var reportsDir = Path.Combine(projDir, "Reports");
                if (!Directory.Exists(reportsDir)) return null;

                var report = Directory.GetFiles(reportsDir, "Analyze Files*.xml")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (report == null) return null;

                XDocument rep;
                try { rep = XDocument.Load(report); }
                catch { return null; }

                // batchTotal aggregates all files; fall back to the first <analyse>.
                var analyse = rep.Descendants().FirstOrDefault(e => e.Name.LocalName == "batchTotal")
                        ?.Elements().FirstOrDefault(e => e.Name.LocalName == "analyse")
                    ?? rep.Descendants().FirstOrDefault(e => e.Name.LocalName == "analyse");
                if (analyse == null) return null;

                var ld = projDoc.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName == "LanguageDirection" && e.Attribute("TargetLanguageCode") != null);

                var sb = new StringBuilder();
                sb.Append("{\"project\":").Append(JsonStr(displayName));
                sb.Append(",\"analysisReport\":").Append(JsonStr(Path.GetFileName(report)));
                sb.Append(",\"languageDirections\":[{");
                sb.Append("\"source\":").Append(JsonStr(ld?.Attribute("SourceLanguageCode")?.Value));
                sb.Append(",\"target\":").Append(JsonStr(ld?.Attribute("TargetLanguageCode")?.Value));

                AppendReportCategory(sb, analyse, "total", "total");
                AppendReportCategory(sb, analyse, "perfect", "perfect");
                AppendReportCategory(sb, analyse, "inContextExact", "inContextExact");
                AppendReportCategory(sb, analyse, "exact", "exact");

                // Sum every fuzzy band into one figure.
                SumReportCategories(sb, analyse, "fuzzy", e => e.Name.LocalName == "fuzzy");
                AppendReportCategory(sb, analyse, "new", "new");
                // Repetitions = in-file + cross-file repeated.
                SumReportCategories(sb, analyse, "repetitions",
                    e => e.Name.LocalName == "repeated" || e.Name.LocalName == "crossFileRepeated");
                AppendReportCategory(sb, analyse, "locked", "locked");

                sb.Append("}]}");
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        // Report attributes are lowercase (words/segments), unlike the .sdlproj inline stats.
        private static void AppendReportCategory(StringBuilder sb, XElement analyse, string jsonKey, string xmlName)
        {
            var elem = analyse.Elements().FirstOrDefault(e => e.Name.LocalName == xmlName);
            if (elem == null) return;
            int.TryParse(elem.Attribute("words")?.Value, out var words);
            int.TryParse(elem.Attribute("segments")?.Value, out var segments);
            sb.Append(",\"").Append(jsonKey).Append("\":{\"words\":").Append(words)
              .Append(",\"segments\":").Append(segments).Append("}");
        }

        private static void SumReportCategories(StringBuilder sb, XElement analyse, string jsonKey,
            Func<XElement, bool> match)
        {
            int words = 0, segments = 0;
            foreach (var e in analyse.Elements().Where(match))
            {
                int.TryParse(e.Attribute("words")?.Value, out var w); words += w;
                int.TryParse(e.Attribute("segments")?.Value, out var s); segments += s;
            }
            sb.Append(",\"").Append(jsonKey).Append("\":{\"words\":").Append(words)
              .Append(",\"segments\":").Append(segments).Append("}");
        }

        private static string BuildProjectStatisticsJson(XDocument projDoc, string displayName)
        {
            var sb = new StringBuilder();
            sb.Append("{\"project\":").Append(JsonStr(displayName));

            // Get per-language-direction statistics
            var langDirs = projDoc.Descendants()
                .Where(e => e.Name.LocalName == "LanguageDirection"
                         && e.Attribute("TargetLanguageCode") != null).ToList();

            sb.Append(",\"languageDirections\":[");
            for (int d = 0; d < langDirs.Count; d++)
            {
                var ld = langDirs[d];
                if (d > 0) sb.Append(",");
                sb.Append("{\"source\":").Append(JsonStr(ld.Attribute("SourceLanguageCode")?.Value));
                sb.Append(",\"target\":").Append(JsonStr(ld.Attribute("TargetLanguageCode")?.Value));

                var stats = ld.Elements().FirstOrDefault(e => e.Name.LocalName == "AnalysisStatistics");
                if (stats != null)
                {
                    AppendStatCategory(sb, stats, "total", "Total");
                    AppendStatCategory(sb, stats, "perfect", "Perfect");
                    AppendStatCategory(sb, stats, "inContextExact", "InContextExact");
                    AppendStatCategory(sb, stats, "exact", "Exact");

                    // Fuzzy bands
                    var fuzzy = stats.Elements().FirstOrDefault(e => e.Name.LocalName == "Fuzzy");
                    if (fuzzy != null)
                    {
                        var fuzzyItems = fuzzy.Elements().Where(e => e.Name.LocalName == "CountData").ToList();
                        int totalFuzzyWords = 0, totalFuzzySegments = 0;
                        foreach (var fi in fuzzyItems)
                        {
                            int.TryParse(fi.Attribute("Words")?.Value, out var fw);
                            int.TryParse(fi.Attribute("Segments")?.Value, out var fs);
                            totalFuzzyWords += fw;
                            totalFuzzySegments += fs;
                        }
                        sb.Append(",\"fuzzy\":{\"words\":").Append(totalFuzzyWords)
                          .Append(",\"segments\":").Append(totalFuzzySegments).Append("}");
                    }

                    AppendStatCategory(sb, stats, "new", "New");
                    AppendStatCategory(sb, stats, "repetitions", "Repetitions");
                    AppendStatCategory(sb, stats, "locked", "Locked");
                }

                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendStatCategory(StringBuilder sb, XElement stats, string jsonKey, string xmlName)
        {
            var elem = stats.Elements().FirstOrDefault(e => e.Name.LocalName == xmlName);
            if (elem == null) return;
            int.TryParse(elem.Attribute("Words")?.Value, out var words);
            int.TryParse(elem.Attribute("Segments")?.Value, out var segments);
            sb.Append(",\"").Append(jsonKey).Append("\":{\"words\":").Append(words)
              .Append(",\"segments\":").Append(segments).Append("}");
        }

        /// <summary>
        /// Reads per-file confirmation statistics directly from a specific
        /// .sdlproj file, bypassing the projects.xml name lookup. Used by the
        /// bridge for the project open in the editor.
        /// </summary>
        public static string GetFileStatusByFile(string sdlprojPath, string displayName)
        {
            if (string.IsNullOrEmpty(sdlprojPath) || !File.Exists(sdlprojPath))
                return JsonError("Project file not found on disk: " + (sdlprojPath ?? "(null)"));
            XDocument doc;
            try { doc = XDocument.Load(sdlprojPath); }
            catch (Exception ex) { return JsonError("Could not read project file: " + ex.Message); }
            return BuildFileStatusJson(doc, displayName);
        }

        private static string GetFileStatus(string projectName)
        {
            var projPath = FindProjectFile(projectName);
            if (projPath == null) return JsonError($"No project found matching '{projectName}'.");

            return BuildFileStatusJson(XDocument.Load(projPath), projectName);
        }

        private static string BuildFileStatusJson(XDocument projDoc, string displayName)
        {
            var sb = new StringBuilder();
            sb.Append("{\"project\":").Append(JsonStr(displayName));
            sb.Append(",\"files\":[");

            var projectFiles = projDoc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectFile"
                         && e.Attribute("Role")?.Value == "Translatable").ToList();

            for (int f = 0; f < projectFiles.Count; f++)
            {
                var pf = projectFiles[f];
                var fileName = pf.Attribute("Name")?.Value ?? "";
                if (f > 0) sb.Append(",");
                sb.Append("{\"name\":").Append(JsonStr(fileName));

                // Get confirmation stats from LanguageFile elements
                var langFiles = pf.Descendants()
                    .Where(e => e.Name.LocalName == "LanguageFile"
                             && e.Attribute("LanguageCode") != null).ToList();

                if (langFiles.Count > 0)
                {
                    sb.Append(",\"languages\":[");
                    for (int l = 0; l < langFiles.Count; l++)
                    {
                        var lf = langFiles[l];
                        if (l > 0) sb.Append(",");
                        sb.Append("{\"language\":").Append(JsonStr(lf.Attribute("LanguageCode")?.Value));

                        var confStats = lf.Elements()
                            .FirstOrDefault(e => e.Name.LocalName == "ConfirmationStatistics");
                        if (confStats != null)
                        {
                            AppendConfirmationStatus(sb, confStats, "notStarted", "Unspecified");
                            AppendConfirmationStatus(sb, confStats, "draft", "Draft");
                            AppendConfirmationStatus(sb, confStats, "translated", "Translated");
                            AppendConfirmationStatus(sb, confStats, "approved", "ApprovedTranslation");
                            AppendConfirmationStatus(sb, confStats, "signedOff", "ApprovedSignOff");
                            AppendConfirmationStatus(sb, confStats, "rejectedTranslation", "RejectedTranslation");
                            AppendConfirmationStatus(sb, confStats, "rejectedSignOff", "RejectedSignOff");
                        }

                        sb.Append("}");
                    }
                    sb.Append("]");
                }

                sb.Append("}");
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendConfirmationStatus(StringBuilder sb, XElement confStats, string jsonKey, string xmlName)
        {
            var elem = confStats.Elements().FirstOrDefault(e => e.Name.LocalName == xmlName);
            if (elem == null) return;
            int.TryParse(elem.Attribute("Words")?.Value, out var words);
            int.TryParse(elem.Attribute("Segments")?.Value, out var segments);
            if (words == 0 && segments == 0) return; // skip zero entries for cleaner output
            sb.Append(",\"").Append(jsonKey).Append("\":{\"words\":").Append(words)
              .Append(",\"segments\":").Append(segments).Append("}");
        }

        private static string ListProjectTermbases(string projectName)
        {
            var projPath = FindProjectFile(projectName);
            if (projPath == null) return JsonError($"No project found matching '{projectName}'.");

            var projDoc = XDocument.Load(projPath);
            var projDir = Path.GetDirectoryName(projPath);

            var sb = new StringBuilder();
            sb.Append("{\"project\":").Append(JsonStr(projectName));
            sb.Append(",\"termbases\":[");

            var tbConfig = projDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "TermbaseConfiguration");

            int count = 0;
            if (tbConfig != null)
            {
                var tbElements = tbConfig.Elements()
                    .Where(e => e.Name.LocalName == "Termbases").ToList();

                foreach (var tb in tbElements)
                {
                    var tbName = tb.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
                    var enabled = tb.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value;
                    var settingsXml = tb.Elements().FirstOrDefault(e => e.Name.LocalName == "SettingsXml")?.Value;

                    // Extract path from the embedded settings XML
                    string tbPath = null;
                    if (!string.IsNullOrEmpty(settingsXml))
                    {
                        try
                        {
                            var settingsDoc = XDocument.Parse(settingsXml);
                            var pathElem = settingsDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Path");
                            tbPath = pathElem?.Value;
                            // Resolve relative paths
                            if (tbPath != null && !Path.IsPathRooted(tbPath) && projDir != null)
                                tbPath = Path.GetFullPath(Path.Combine(projDir, tbPath));
                        }
                        catch { }
                    }

                    if (count > 0) sb.Append(",");
                    sb.Append("{\"name\":").Append(JsonStr(tbName));
                    sb.Append(",\"enabled\":").Append(enabled?.ToLowerInvariant() == "true" ? "true" : "false");
                    if (tbPath != null)
                        sb.Append(",\"path\":").Append(JsonStr(tbPath));
                    sb.Append("}");
                    count++;
                }

                // Language index mappings
                var mappings = tbConfig.Elements()
                    .Where(e => e.Name.LocalName == "LanguageIndexMappings").ToList();
                if (mappings.Count > 0)
                {
                    sb.Append("],\"languageMappings\":[");
                    for (int i = 0; i < mappings.Count; i++)
                    {
                        var lang = mappings[i].Elements().FirstOrDefault(e => e.Name.LocalName == "Language")?.Value;
                        var idx = mappings[i].Elements().FirstOrDefault(e => e.Name.LocalName == "Index")?.Value;
                        if (i > 0) sb.Append(",");
                        sb.Append("{\"language\":").Append(JsonStr(lang));
                        sb.Append(",\"index\":").Append(JsonStr(idx)).Append("}");
                    }
                }
            }

            sb.Append("],\"total\":").Append(count).Append("}");
            return sb.ToString();
        }

        private static string GetTmInfo(string tmName)
        {
            if (string.IsNullOrWhiteSpace(tmName))
                return JsonError("TM name is required.");

            var tmPath = FindTmFile(tmName);
            if (tmPath == null)
                return JsonError($"No translation memory found matching '{tmName}'.");

            return ReadTmMetadata(tmPath);
        }

        private static string SearchTm(string tmName, string searchText, int? maxResults)
        {
            if (string.IsNullOrWhiteSpace(tmName))
                return JsonError("TM name is required.");
            if (string.IsNullOrWhiteSpace(searchText))
                return JsonError("Search text is required.");

            var tmPath = FindTmFile(tmName);
            if (tmPath == null)
                return JsonError($"No translation memory found matching '{tmName}'.");

            int limit = Math.Min(maxResults ?? 20, 50);

            try
            {
                var connectionString = $"Data Source={tmPath};Mode=ReadOnly";
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                {
                    conn.Open();

                    // Search source_segment and target_segment XML for the text
                    // The text is stored inside <Value>...</Value> tags
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            SELECT source_segment, target_segment, creation_date, change_date, usage_counter
                            FROM translation_units
                            WHERE source_segment LIKE @search OR target_segment LIKE @search
                            ORDER BY change_date DESC
                            LIMIT @limit";
                        cmd.Parameters.AddWithValue("@search", $"%{searchText}%");
                        cmd.Parameters.AddWithValue("@limit", limit);

                        var sb = new StringBuilder();
                        sb.Append("{\"tm\":").Append(JsonStr(Path.GetFileNameWithoutExtension(tmPath)));
                        sb.Append(",\"searchText\":").Append(JsonStr(searchText));
                        sb.Append(",\"results\":[");

                        int count = 0;
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var sourceXml = reader.GetString(0);
                                var targetXml = reader.GetString(1);
                                var source = ExtractSegmentText(sourceXml);
                                var target = ExtractSegmentText(targetXml);

                                // Only include if the plain text actually contains the search term
                                if (source.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0
                                    && target.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;

                                if (count > 0) sb.Append(",");
                                sb.Append("{\"source\":").Append(JsonStr(source));
                                sb.Append(",\"target\":").Append(JsonStr(target));

                                if (!reader.IsDBNull(2))
                                    sb.Append(",\"created\":").Append(JsonStr(FormatDate(reader.GetString(2))));
                                if (!reader.IsDBNull(4))
                                {
                                    var usage = reader.GetInt32(4);
                                    if (usage > 0)
                                        sb.Append(",\"usageCount\":").Append(usage);
                                }

                                sb.Append("}");
                                count++;
                            }
                        }

                        sb.Append("],\"total\":").Append(count).Append("}");
                        return sb.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                return JsonError($"Error searching TM: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts plain text from a Trados TM segment XML.
        /// Segments are stored as: &lt;Segment&gt;&lt;Elements&gt;&lt;Text&gt;&lt;Value&gt;...&lt;/Value&gt;&lt;/Text&gt;...
        /// </summary>
        private static string ExtractSegmentText(string segmentXml)
        {
            if (string.IsNullOrEmpty(segmentXml)) return "";
            try
            {
                var doc = XDocument.Parse(segmentXml);
                var values = doc.Descendants()
                    .Where(e => e.Name.LocalName == "Value")
                    .Select(e => e.Value);
                return string.Join("", values);
            }
            catch
            {
                // Fallback: strip XML tags with regex
                return Regex.Replace(segmentXml, "<[^>]+>", "").Trim();
            }
        }

        /// <summary>
        /// Reads TM metadata (language pair, segment count, etc.) from the SQLite file.
        /// </summary>
        private static string ReadTmMetadata(string tmPath)
        {
            try
            {
                var connectionString = $"Data Source={tmPath};Mode=ReadOnly";
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            SELECT name, source_language, target_language, tucount,
                                   description, creation_date, creation_user
                            FROM translation_memories
                            LIMIT 1";

                        using (var reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read())
                                return JsonError("TM file contains no metadata.");

                            var sb = new StringBuilder();
                            sb.Append("{");
                            sb.Append("\"name\":").Append(JsonStr(reader.IsDBNull(0) ? null : reader.GetString(0)));
                            sb.Append(",\"sourceLanguage\":").Append(JsonStr(reader.IsDBNull(1) ? null : reader.GetString(1)));
                            sb.Append(",\"targetLanguage\":").Append(reader.IsDBNull(2) ? "null" : JsonStr(reader.GetString(2)));
                            sb.Append(",\"segmentCount\":").Append(reader.IsDBNull(3) ? 0 : reader.GetInt32(3));

                            if (!reader.IsDBNull(4) && !string.IsNullOrEmpty(reader.GetString(4)))
                                sb.Append(",\"description\":").Append(JsonStr(reader.GetString(4)));
                            if (!reader.IsDBNull(5))
                                sb.Append(",\"created\":").Append(JsonStr(FormatDate(reader.GetString(5))));
                            if (!reader.IsDBNull(6) && !string.IsNullOrEmpty(reader.GetString(6)))
                                sb.Append(",\"createdBy\":").Append(JsonStr(reader.GetString(6)));

                            sb.Append(",\"path\":").Append(JsonStr(tmPath));

                            // File size
                            var fileInfo = new FileInfo(tmPath);
                            if (fileInfo.Exists)
                                sb.Append(",\"fileSizeMB\":").Append((fileInfo.Length / (1024.0 * 1024)).ToString("F1"));

                            sb.Append("}");
                            return sb.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return JsonError($"Error reading TM: {ex.Message}");
            }
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Finds a .sdlproj file by project name (partial match).
        /// Returns the full path, or null if not found.
        /// </summary>
        private static string FindProjectFile(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName)) return null;

            var searchLower = projectName.ToLowerInvariant();
            foreach (var xmlPath in GetProjectsXmlPaths())
            {
                XDocument doc;
                try { doc = XDocument.Load(xmlPath); }
                catch { continue; }
                var match = doc.Descendants("ProjectListItem").FirstOrDefault(i =>
                {
                    var name = i.Element("ProjectInfo")?.Attribute("Name")?.Value;
                    return name != null && name.ToLowerInvariant().Contains(searchLower);
                });
                if (match == null) continue;

                var projectFilePath = match.Attribute("ProjectFilePath")?.Value;
                var projPath = ResolveProjectPath(projectFilePath, xmlPath);
                if (projPath != null && File.Exists(projPath)) return projPath;
            }
            return null;
        }

        /// <summary>
        /// Finds a .sdltm file by TM name (partial match).
        /// Extracts TM paths from .sdlproj files (the authoritative source),
        /// also scans the central TM folder and project Tm/ subfolders.
        /// Works regardless of where users store their TMs.
        /// </summary>
        private static string FindTmFile(string tmName)
        {
            if (string.IsNullOrWhiteSpace(tmName)) return null;
            var searchLower = tmName.ToLowerInvariant();

            // Collect all known TM file paths
            var candidates = new List<string>();

            // 1. Central TM folders (may be empty if user moved TMs elsewhere)
            var docsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var tmFolders = new[]
            {
                Path.Combine(docsFolder, "Studio 2024", "Translation Memories"),
                Path.Combine(docsFolder, "Studio 2022", "Translation Memories")
            };
            foreach (var folder in tmFolders)
            {
                if (!Directory.Exists(folder)) continue;
                try { candidates.AddRange(Directory.GetFiles(folder, "*.sdltm", SearchOption.AllDirectories)); }
                catch { }
            }

            // 2. Extract TM paths from all .sdlproj files (handles custom locations)
            var xmlPath = GetProjectsXmlPath();
            if (xmlPath != null && File.Exists(xmlPath))
            {
                var doc = XDocument.Load(xmlPath);
                var scannedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in doc.Descendants("ProjectListItem"))
                {
                    var pfp = item.Attribute("ProjectFilePath")?.Value;
                    var projPath = ResolveProjectPath(pfp, xmlPath);
                    if (projPath == null || !File.Exists(projPath)) continue;

                    var projDir = Path.GetDirectoryName(projPath);
                    if (projDir == null) continue;

                    // Extract TM URIs from the .sdlproj
                    try
                    {
                        var projDoc = XDocument.Load(projPath);
                        var tmUris = projDoc.Descendants()
                            .Where(e => e.Name.LocalName == "MainTranslationProviderItem"
                                     || e.Name.LocalName == "ProjectTranslationProviderItem")
                            .Select(e => e.Attribute("Uri")?.Value)
                            .Where(u => u != null && u.Contains(".sdltm"))
                            .Distinct();

                        foreach (var uri in tmUris)
                        {
                            var path = ResolveTmUri(uri, projDir);
                            if (path != null && File.Exists(path))
                                candidates.Add(path);
                        }
                    }
                    catch { }

                    // Also scan project's Tm subfolder
                    if (scannedDirs.Add(projDir))
                    {
                        var tmSubDir = Path.Combine(projDir, "Tm");
                        if (Directory.Exists(tmSubDir))
                        {
                            try { candidates.AddRange(Directory.GetFiles(tmSubDir, "*.sdltm", SearchOption.AllDirectories)); }
                            catch { }
                        }
                    }
                }
            }

            // Deduplicate and search by name
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in candidates)
            {
                string fullPath;
                try { fullPath = Path.GetFullPath(file); } catch { continue; }
                if (!seen.Add(fullPath)) continue;

                var fileName = Path.GetFileNameWithoutExtension(fullPath).ToLowerInvariant();
                if (fileName.Contains(searchLower))
                    return fullPath;
            }

            return null;
        }

        /// <summary>
        /// Resolves a TM URI from an .sdlproj file to an absolute file path.
        /// Handles sdltm.file:/// and file:/// prefixes, and relative paths.
        /// </summary>
        private static string ResolveTmUri(string uri, string projectDir)
        {
            if (string.IsNullOrEmpty(uri)) return null;

            var path = uri;
            if (path.StartsWith("sdltm.file:///"))
                path = path.Substring("sdltm.file:///".Length);
            else if (path.StartsWith("file:///"))
                path = path.Substring("file:///".Length);

            // URI-decode (e.g. %20 → space)
            try { path = Uri.UnescapeDataString(path); } catch { }

            if (!Path.IsPathRooted(path))
                path = Path.Combine(projectDir, path);

            try { return Path.GetFullPath(path); } catch { return path; }
        }

        private static string GetProjectsXmlPath()
        {
            foreach (var p in GetProjectsXmlPaths())
                return p;
            return null;
        }

        /// <summary>
        /// All "Studio *" folders under Documents, newest Studio first. Trados
        /// names this folder after the product edition and the name VARIES –
        /// "Studio 2026 Release" (not "Studio 2026"), "Studio 2024", "Studio 2022"
        /// – so hardcoding names silently misses whole versions. Enumerate instead.
        /// </summary>
        private static List<string> GetStudioDocumentFolders()
        {
            var result = new List<string>();
            try
            {
                var docsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (Directory.Exists(docsFolder))
                    result.AddRange(Directory.GetDirectories(docsFolder, "Studio *"));
            }
            catch { }
            result.Sort((a, b) => StudioYearOf(b).CompareTo(StudioYearOf(a)));
            return result;
        }

        private static int StudioYearOf(string dir)
        {
            var m = Regex.Match(Path.GetFileName(dir) ?? "", @"\d{4}");
            return m.Success ? int.Parse(m.Value) : 0;
        }

        /// <summary>
        /// ALL existing Trados project registries on this machine, newest Studio
        /// first. Each Studio major keeps its own projects.xml, so anything that
        /// answers "what projects are there" must read every one of them – reading
        /// only the first misses projects registered under another version.
        /// </summary>
        private static List<string> GetProjectsXmlPaths()
        {
            var result = new List<string>();
            foreach (var folder in GetStudioDocumentFolders())
            {
                var p = Path.Combine(folder, "Projects", "projects.xml");
                if (File.Exists(p)) result.Add(p);
            }
            return result;
        }

        /// <summary>"Studio 2026" from ...\Documents\Studio 2026\Projects\projects.xml.</summary>
        private static string StudioLabelFromXmlPath(string xmlPath)
        {
            try { return Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(xmlPath))); }
            catch { return null; }
        }

        private static string ResolveProjectPath(string projectFilePath, string xmlPath)
        {
            if (string.IsNullOrEmpty(projectFilePath)) return null;
            if (Path.IsPathRooted(projectFilePath)) return projectFilePath;

            // Relative to the projects.xml folder
            var xmlDir = Path.GetDirectoryName(xmlPath);
            return xmlDir != null ? Path.Combine(xmlDir, projectFilePath) : projectFilePath;
        }

        private static string MapStatus(string status)
        {
            switch (status)
            {
                case "InProgress": return "Started";
                case "Completed": return "Completed";
                case "Archived": return "Archived";
                default: return status ?? "Unknown";
            }
        }

        private static string FormatDate(string isoDate)
        {
            if (string.IsNullOrEmpty(isoDate)) return "";
            if (DateTime.TryParse(isoDate, out var dt))
                return dt.ToString("d MMM yyyy");
            return isoDate;
        }

        private static string JsonStr(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:X4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string JsonError(string message)
        {
            return "{\"error\":" + JsonStr(message) + "}";
        }

        /// <summary>
        /// Extracts a string field from a simple JSON object.
        /// Returns null if the field is not found.
        /// </summary>
        private static string ExtractJsonField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var pattern = $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"";
            var match = Regex.Match(json, pattern);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Extracts an integer field from a simple JSON object.
        /// Returns null if the field is not found.
        /// </summary>
        private static int? ExtractJsonInt(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var pattern = $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*(\\d+)";
            var match = Regex.Match(json, pattern);
            return match.Success && int.TryParse(match.Groups[1].Value, out var val) ? val : (int?)null;
        }
    }
}
