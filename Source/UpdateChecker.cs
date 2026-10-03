// Fields of the JSON DTOs below are filled by JsonUtility, which the compiler cannot see.
#pragma warning disable CS0649
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace RaymarchedPlumeTrails
{
    [Serializable] internal class GhAsset { public string name; public string browser_download_url; public long size; }
    [Serializable] internal class GhRelease
    {
        public string tag_name; public string name; public string body; public string html_url;
        public bool draft; public bool prerelease; public GhAsset[] assets;
    }

    // Checks the mod's GitHub releases when KSP starts. If a newer version exists a window offers
    //   Download            - fetches the release zip; it is installed automatically when KSP is closed
    //                         (a loaded DLL cannot be replaced while the game runs)
    //   Skip this version   - never asks about this version again
    //   Remind next launch  - closes the window; it comes back at the next start
    //   Hide                - shrinks to a small badge for this session
    // Settings live in PluginData/update.cfg:  CheckForUpdates = true/false, SkipVersion = x.y.z,
    // ReleaseApiUrl = (override, for testing).
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class UpdateChecker : MonoBehaviour
    {
        private enum St { Idle, Checking, Available, Downloading, Staged, Error }

        private St st = St.Idle;
        private bool windowOpen, minimized;
        private Rect win = new Rect(0, 0, 540, 420);
        private Vector2 scroll;
        private string localVer = "0.0.0", remoteVer = "", body = "", assetUrl, assetName, htmlUrl = "", error = "";
        private float progress;
        private string updatedToast;
        private float toastUntil;
        private bool allowFileUrls;
        private Texture2D white;

        private static string ModDir { get { return Path.GetDirectoryName(AssetLoader.AssemblyDirectory()); } }
        private static string DataDir { get { return Path.Combine(ModDir, "PluginData"); } }
        private static string UpdateDir { get { return Path.Combine(DataDir, "update"); } }
        private static string CfgPath { get { return Path.Combine(DataDir, "update.cfg"); } }

        private void Start()
        {
            white = Texture2D.whiteTexture;
            string versionFile = Path.Combine(ModDir, "RaymarchedPlumeTrails.version");
            string vtext = File.Exists(versionFile) ? File.ReadAllText(versionFile) : "";
            localVer = ParseVersionFile(vtext);

            string flag = Path.Combine(UpdateDir, "updated.txt");
            if (File.Exists(flag))
            {
                try { updatedToast = "Updated to v" + File.ReadAllText(flag).Trim(); File.Delete(flag); toastUntil = Time.realtimeSinceStartup + 12f; } catch { }
            }

            ConfigNode cfg = File.Exists(CfgPath) ? ConfigNode.Load(CfgPath) : null;
            bool check = true;
            string skip = "", apiOverride = "";
            if (cfg != null)
            {
                if (cfg.HasValue("CheckForUpdates")) bool.TryParse(cfg.GetValue("CheckForUpdates"), out check);
                if (cfg.HasValue("SkipVersion")) skip = cfg.GetValue("SkipVersion");
                if (cfg.HasValue("ReleaseApiUrl")) apiOverride = cfg.GetValue("ReleaseApiUrl");
            }
            if (!check) return;

            string owner = Regex.Match(vtext, "\"USERNAME\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            string repo = Regex.Match(vtext, "\"REPOSITORY\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            string api = apiOverride;
            if (string.IsNullOrEmpty(api))
            {
                if (owner.Length == 0 || repo.Length == 0) return;
                api = "https://api.github.com/repos/" + owner + "/" + repo + "/releases/latest";
            }
            else allowFileUrls = true;
            StartCoroutine(Check(api, skip, owner, repo));
        }

        private static string ParseVersionFile(string t)
        {
            Func<string, string> n = k => { var m = Regex.Match(t, "\"" + k + "\"\\s*:\\s*(\\d+)"); return m.Success ? m.Groups[1].Value : "0"; };
            return n("MAJOR") + "." + n("MINOR") + "." + n("PATCH");
        }

        private static Version ParseTag(string tag)
        {
            Match m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)\.(\d+)");
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
        }

        private IEnumerator Check(string api, string skip, string owner, string repo)
        {
            st = St.Checking;
            GhRelease rel = null;
            using (UnityWebRequest req = UnityWebRequest.Get(api))
            {
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.SetRequestHeader("User-Agent", "RaymarchedPlumeTrails-updater");
                req.timeout = 15;
                yield return req.SendWebRequest();
                if (req.isNetworkError || req.isHttpError)
                {
                    UnityEngine.Debug.Log("[PlumeTrails] update check: " + req.error + " (" + api + ")");
                    st = St.Idle; yield break;
                }
                try { rel = JsonUtility.FromJson<GhRelease>(req.downloadHandler.text); } catch { }
            }
            if (rel == null || rel.draft || string.IsNullOrEmpty(rel.tag_name)) { st = St.Idle; yield break; }

            Version remote = ParseTag(rel.tag_name), local = ParseTag(localVer);
            if (remote == null || local == null || remote <= local) { st = St.Idle; yield break; }
            remoteVer = remote.ToString();
            if (remoteVer == skip) { UnityEngine.Debug.Log("[PlumeTrails] update " + remoteVer + " skipped by the user"); st = St.Idle; yield break; }

            GhAsset pick = null;
            if (rel.assets != null)
                foreach (GhAsset a in rel.assets)
                    if (a != null && a.name != null && a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && (pick == null || a.name.IndexOf("RaymarchedPlumeTrails", StringComparison.OrdinalIgnoreCase) >= 0)) pick = a;
            if (pick == null) { UnityEngine.Debug.Log("[PlumeTrails] release " + rel.tag_name + " has no .zip asset"); st = St.Idle; yield break; }

            // only ever download from this mod's own GitHub repo (or a file:// override for testing)
            string okPrefix = "https://github.com/" + owner + "/" + repo + "/";
            if (!(pick.browser_download_url ?? "").StartsWith(okPrefix, StringComparison.OrdinalIgnoreCase)
                && !(allowFileUrls && (pick.browser_download_url ?? "").StartsWith("file:///", StringComparison.OrdinalIgnoreCase)))
            { UnityEngine.Debug.LogWarning("[PlumeTrails] update asset URL is not from the mod's repo, ignoring: " + pick.browser_download_url); st = St.Idle; yield break; }

            assetUrl = pick.browser_download_url;
            assetName = Path.GetFileName(pick.name);
            htmlUrl = rel.html_url ?? "";
            body = (rel.body ?? "").Trim();
            if (body.Length == 0) body = "(no release notes)";
            st = St.Available;
            windowOpen = true;
            win.x = (Screen.width - win.width) * 0.5f;
            win.y = (Screen.height - win.height) * 0.35f;
            UnityEngine.Debug.Log("[PlumeTrails] update available: " + localVer + " -> " + remoteVer);
        }

        private IEnumerator Download()
        {
            st = St.Downloading; progress = 0f; error = "";
            Directory.CreateDirectory(UpdateDir);
            string zip = Path.Combine(UpdateDir, assetName);
            using (UnityWebRequest req = UnityWebRequest.Get(assetUrl))
            {
                req.downloadHandler = new DownloadHandlerFile(zip) { removeFileOnAbort = true };
                req.SetRequestHeader("User-Agent", "RaymarchedPlumeTrails-updater");
                UnityWebRequestAsyncOperation op = req.SendWebRequest();
                while (!op.isDone) { progress = req.downloadProgress; yield return null; }
                if (req.isNetworkError || req.isHttpError) { error = req.error; st = St.Error; yield break; }
            }
            // a real zip starts with "PK"
            byte[] head = new byte[2];
            try { using (var f = File.OpenRead(zip)) f.Read(head, 0, 2); } catch (Exception e) { error = e.Message; st = St.Error; yield break; }
            if (head[0] != (byte)'P' || head[1] != (byte)'K') { error = "the downloaded file is not a zip"; st = St.Error; yield break; }

            try { StartInstallOnExit(zip); st = St.Staged; }
            catch (Exception e) { error = "could not schedule the install: " + e.Message; st = St.Error; }
        }

        // A loaded plugin cannot be overwritten while KSP runs, so a hidden helper waits for this process to
        // exit and then copies GameData/RaymarchedPlumeTrails out of the zip over the install. Only entries under
        // that prefix are extracted, never anything with "..", and PluginData (the user's tuning) is left alone.
        private void StartInstallOnExit(string zip)
        {
            string script = Path.Combine(UpdateDir, "apply_update.ps1");
            string log = Path.Combine(UpdateDir, "apply.log");
            File.WriteAllText(script, ApplyScript);
            string args = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\""
                + " -KspPid " + Process.GetCurrentProcess().Id
                + " -Zip \"" + zip + "\" -Target \"" + ModDir + "\" -Log \"" + log + "\" -Ver \"" + remoteVer + "\"";
            var psi = new ProcessStartInfo("powershell.exe", args) { CreateNoWindow = true, UseShellExecute = false };
            Process.Start(psi);
            UnityEngine.Debug.Log("[PlumeTrails] update " + remoteVer + " downloaded; installs when KSP closes (" + zip + ")");
        }

        private const string ApplyScript = @"param([int]$KspPid,[string]$Zip,[string]$Target,[string]$Log,[string]$Ver)
$ErrorActionPreference = 'Stop'
function L($m) { Add-Content -Path $Log -Value ((Get-Date -Format s) + '  ' + $m) }
try {
  L ""waiting for KSP ($KspPid) to exit""
  try { Wait-Process -Id $KspPid -ErrorAction SilentlyContinue } catch { }
  Start-Sleep -Seconds 3
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $prefix = 'GameData/RaymarchedPlumeTrails/'
  $tmp = Join-Path ([IO.Path]::GetTempPath()) ('rpt_update_' + [guid]::NewGuid())
  New-Item -ItemType Directory -Path $tmp | Out-Null
  $za = [IO.Compression.ZipFile]::OpenRead($Zip)
  $n = 0
  foreach ($e in $za.Entries) {
    $name = $e.FullName.Replace('\', '/')
    if (-not $name.StartsWith($prefix) -or $name.EndsWith('/')) { continue }
    $rel = $name.Substring($prefix.Length)
    if ($rel.Contains('..') -or $rel.StartsWith('PluginData/')) { continue }
    $dest = [IO.Path]::GetFullPath((Join-Path $tmp $rel))
    if (-not $dest.StartsWith($tmp)) { continue }
    New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dest, $true)
    $n++
  }
  $za.Dispose()
  if ($n -eq 0 -or -not (Test-Path (Join-Path $tmp 'Plugins'))) { throw 'archive has no GameData/RaymarchedPlumeTrails/Plugins' }
  Copy-Item -Path (Join-Path $tmp '*') -Destination $Target -Recurse -Force
  Remove-Item -Recurse -Force $tmp
  Set-Content -Path (Join-Path (Split-Path $Log) 'updated.txt') -Value $Ver
  L (""installed $n files, version $Ver"")
} catch { L ('FAILED: ' + $_.Exception.Message) }
";

        private void SaveCfg(string skip)
        {
            try
            {
                ConfigNode cfg = File.Exists(CfgPath) ? ConfigNode.Load(CfgPath) : new ConfigNode();
                if (cfg == null) cfg = new ConfigNode();
                if (cfg.HasValue("SkipVersion")) cfg.SetValue("SkipVersion", skip); else cfg.AddValue("SkipVersion", skip);
                if (!cfg.HasValue("CheckForUpdates")) cfg.AddValue("CheckForUpdates", "True");
                Directory.CreateDirectory(DataDir);
                cfg.Save(CfgPath, "Raymarched Plume Trails updater. SkipVersion = a version that is never offered again.");
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[PlumeTrails] could not save update.cfg: " + e.Message); }
        }

        private static void Fill(Rect r, Color c)
        {
            Color old = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = old;
        }

        private void OnGUI()
        {
            if (updatedToast != null && Time.realtimeSinceStartup < toastUntil)
            {
                Rect r = new Rect(Screen.width - 260, 12, 248, 30);
                Fill(r, new Color(0.10f, 0.35f, 0.18f, 0.92f));
                GUI.Label(new Rect(r.x + 10, r.y + 5, r.width - 12, 22), "Plume Trails: " + updatedToast);
            }
            if (st != St.Available && st != St.Downloading && st != St.Staged && st != St.Error) return;

            if (minimized || !windowOpen)
            {
                if (!windowOpen) return;
                Rect pill = new Rect(Screen.width - 250, 12, 238, 28);
                if (GUI.Button(pill, "Plume Trails: update v" + remoteVer + " available")) minimized = false;
                return;
            }
            win = GUILayout.Window(834622, win, Draw, "Raymarched Plume Trails - update available");
        }

        private void Draw(int id)
        {
            GUILayout.Space(4);
            GUILayout.Label("<size=15><b>v" + localVer + "  →  v" + remoteVer + "</b></size>", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Space(2);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(210));
            GUILayout.Label(body, new GUIStyle(GUI.skin.label) { wordWrap = true });
            GUILayout.EndScrollView();
            GUILayout.Space(6);

            if (st == St.Downloading)
            {
                Rect bar = GUILayoutUtility.GetRect(10, 16, GUILayout.ExpandWidth(true));
                Fill(bar, new Color(0f, 0f, 0f, 0.5f));
                Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(progress), bar.height), new Color(0.3f, 0.7f, 1f, 0.9f));
                GUILayout.Label("Downloading... " + Mathf.RoundToInt(progress * 100f) + "%");
            }
            else if (st == St.Staged)
            {
                GUILayout.Label("<color=#7CFC9A>Downloaded.</color> v" + remoteVer + " installs automatically when you close KSP.", new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
                if (GUILayout.Button("OK")) { minimized = true; }
            }
            else if (st == St.Error)
            {
                GUILayout.Label("<color=#FF8080>Failed:</color> " + error, new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Try again")) StartCoroutine(Download());
                if (GUILayout.Button("Hide")) minimized = true;
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Download", GUILayout.Height(30))) StartCoroutine(Download());
                if (GUILayout.Button("Skip this version", GUILayout.Height(30))) { SaveCfg(remoteVer); windowOpen = false; }
                if (GUILayout.Button("Remind next launch", GUILayout.Height(30))) { windowOpen = false; }
                if (GUILayout.Button("Hide", GUILayout.Height(30))) { minimized = true; }
                GUILayout.EndHorizontal();
                if (htmlUrl.Length > 0 && GUILayout.Button("Open release page")) Application.OpenURL(htmlUrl);
            }
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }
    }
}
