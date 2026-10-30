#pragma warning disable CS0649 // filled by JsonUtility
using System;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Installs UnitySplats and its WebP dependency without git: WebP comes from OpenUPM (a scoped registry),
    /// UnitySplats is downloaded as a zip and embedded in Packages/. Lives in the ungated assembly because
    /// everything that needs UnitySplats cannot compile until this has run.
    /// </summary>
    public sealed class SplatDependencyStep : ISetupStep
    {
        public const string UnitySplatsId = "com.arloopa.unitysplats";
        public const string WebpId = "com.netpyoung.webp";
        const string k_zipUrl = "https://codeload.github.com/arloopa/UnitySplats/zip/refs/tags/v2.0.0";

        static bool s_dirty = true, s_installed;
        static bool s_busy;
        static UnityWebRequest s_request;
        static string s_tempDir;

        static SplatDependencyStep()
        {
            Events.registeredPackages += _ => s_dirty = true;
        }

        public int Order => 10;
        public string Title => "Splat packages";
        public string ActionLabel => "Install";

        public SetupStatus GetStatus(out string detail)
        {
            if (s_dirty)
            {
                s_dirty = false;
                s_installed = false;
                bool splats = false, webp = false;
                foreach (var p in PackageInfo.GetAllRegisteredPackages())
                {
                    splats |= p.name == UnitySplatsId;
                    webp |= p.name == WebpId;
                }
                s_installed = splats && webp;
            }
            if (s_busy) { detail = "Downloading UnitySplats…"; return SetupStatus.Blocked; }
            if (s_installed) { detail = "UnitySplats and WebP are installed."; return SetupStatus.Done; }
            detail = "UnitySplats (Gaussian splat rendering) and WebP are missing. Needs an internet connection, no git required.";
            return SetupStatus.Needed;
        }

        public void Run()
        {
            if (!HasPackage(WebpId) && SplatManifest.EnsureOpenUpmScope(WebpId))
                Debug.Log("[StageSettAR] Added the OpenUPM registry (scope " + WebpId + ") to Packages/manifest.json");
            if (!HasPackage(UnitySplatsId)) Download();
            else Client.Resolve();
        }

        static bool HasPackage(string id)
        {
            foreach (var p in PackageInfo.GetAllRegisteredPackages()) if (p.name == id) return true;
            return false;
        }

        /// <summary>Alternative to the download: use a local clone (e.g. a fork) through a file: dependency.</summary>
        public static void UseLocalFolder()
        {
            try
            {
                var dir = EditorUtility.OpenFolderPanel("Folder containing the UnitySplats package.json", "", "");
                if (string.IsNullOrEmpty(dir)) return;
                if (!IsUnitySplatsFolder(dir))
                {
                    Fail($"'{dir}' does not contain a package.json named {UnitySplatsId}. Pick the folder that holds package.json.");
                    return;
                }
                if (Directory.Exists(SplatVcsIgnore.PackageDir) &&
                    !EditorUtility.DisplayDialog("StageSettAR", "Packages/" + UnitySplatsId + " already exists and would override the local folder. Delete it?", "Delete", "Cancel"))
                    return;
                if (Directory.Exists(SplatVcsIgnore.PackageDir)) Directory.Delete(SplatVcsIgnore.PackageDir, true);

                var rel = Path.GetRelativePath(SplatManifest.PackagesDir, dir).Replace('\\', '/');
                SplatManifest.EnsureOpenUpmScope(WebpId);
                SplatManifest.SetDependency(UnitySplatsId, "file:" + rel);
                Debug.Log("[StageSettAR] Using local UnitySplats from " + rel);
                Client.Resolve();
            }
            catch (Exception e) { Fail("Could not use the local folder: " + e.Message, e); }
        }

        static bool IsUnitySplatsFolder(string dir)
        {
            var file = Path.Combine(dir, "package.json");
            return File.Exists(file) && JsonUtility.FromJson<PackageJson>(File.ReadAllText(file))?.name == UnitySplatsId;
        }

        [Serializable] class PackageJson { public string name; }

        static void Download()
        {
            var target = SplatVcsIgnore.PackageDir;
            if (Directory.Exists(target) &&
                !EditorUtility.DisplayDialog("StageSettAR", "Packages/" + UnitySplatsId + " already exists. Replace it with a fresh download?", "Replace", "Cancel"))
                return;

            s_tempDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Temp", "StageSettAR");
            if (Directory.Exists(s_tempDir)) Directory.Delete(s_tempDir, true);
            Directory.CreateDirectory(s_tempDir);

            s_request = new UnityWebRequest(k_zipUrl, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = new DownloadHandlerFile(Path.Combine(s_tempDir, "unitysplats.zip")) { removeFileOnAbort = true },
            };
            s_request.SendWebRequest();
            s_busy = true;
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (!s_request.isDone)
            {
                if (EditorUtility.DisplayCancelableProgressBar("StageSettAR", "Downloading UnitySplats…", s_request.downloadProgress))
                {
                    s_request.Abort();
                    Finish();
                    Debug.Log("[StageSettAR] UnitySplats download cancelled.");
                }
                return;
            }

            var result = s_request.result;
            var code = s_request.responseCode;
            var error = s_request.error;
            Finish();
            if (result == UnityWebRequest.Result.Success) Install();
            else if (result == UnityWebRequest.Result.ConnectionError)
                Fail($"Could not reach GitHub ({error}). Check your internet connection and try again.");
            else
                Fail($"Downloading UnitySplats failed (HTTP {code}: {error}). GitHub may be unreachable or blocked on this network.");
        }

        static void Finish()
        {
            EditorApplication.update -= Poll;
            EditorUtility.ClearProgressBar();
            s_request.Dispose();
            s_request = null;
            s_busy = false;
        }

        static void Install()
        {
            try
            {
                EditorUtility.DisplayProgressBar("StageSettAR", "Unpacking UnitySplats…", 0.9f);
                var extract = Path.Combine(s_tempDir, "extract");
                Directory.CreateDirectory(extract);
                using (var zip = ZipFile.OpenRead(Path.Combine(s_tempDir, "unitysplats.zip"))) Extract(zip, extract);

                // GitHub archives hold a single top-level folder (UnitySplats-2.0.0/).
                var root = extract;
                var dirs = Directory.GetDirectories(extract);
                if (dirs.Length == 1 && Directory.GetFiles(extract).Length == 0) root = dirs[0];
                if (!IsUnitySplatsFolder(root))
                    throw new InvalidDataException($"The download has no package.json named {UnitySplatsId}.");

                // An embedded package's tests compile in the user's project, and UnitySplats 2.0.0's test assembly
                // uses Unity.Mathematics without referencing it, which fails the whole compile. Nobody here runs them.
                var tests = Path.Combine(root, "Tests");
                if (Directory.Exists(tests)) Directory.Delete(tests, true);
                if (File.Exists(tests + ".meta")) File.Delete(tests + ".meta");

                var target = SplatVcsIgnore.PackageDir;
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(root, target);
                SplatVcsIgnore.Apply();
                Directory.Delete(s_tempDir, true);

                Debug.Log("[StageSettAR] Installed UnitySplats into Packages/" + UnitySplatsId);
                AssetDatabase.Refresh();
                Client.Resolve();
            }
            catch (Exception e) { Fail("Could not install UnitySplats: " + e.Message, e); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static void Extract(ZipArchive zip, string dest)
        {
            var prefix = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
            foreach (var e in zip.Entries)
            {
                if (e.Name.Length == 0) continue; // directory entry
                var path = Path.GetFullPath(Path.Combine(dest, e.FullName));
                if (!path.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidDataException("Zip entry escapes the target folder: " + e.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                e.ExtractToFile(path, true);
            }
        }

        static void Fail(string message, Exception e = null)
        {
            if (e != null) Debug.LogException(e);
            Debug.LogError("[StageSettAR] " + message);
            EditorUtility.DisplayDialog("StageSettAR", message, "OK");
        }
    }
}
