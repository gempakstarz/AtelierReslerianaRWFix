using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Doorstop
{
    public static class Entrypoint
    {
        public static void Start()
        {
            AtelierReslerianaRWFix.FovFixPlugin.Initialize();
        }
    }
}

namespace AtelierReslerianaRWFix
{
    internal static class FovFixPlugin
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("dobby.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DobbyHook(IntPtr address, IntPtr replaceCall, out IntPtr originCall);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ResolveIcallDelegate(IntPtr namePtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DomainGetDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr Il2CppStringCharsDelegate(IntPtr strPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetFovDelegate(IntPtr camPtr, float value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate float GetFovDelegate(IntPtr camPtr);

        // Unity Engine Inspection Delegates
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte GetBoolPropDelegate(IntPtr objPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetPtrPropDelegate(IntPtr objPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GetGameObjectSceneInjectedDelegate(IntPtr goPtr, out int sceneHandle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetSceneNameDelegate(int sceneHandle);

        private static SetFovDelegate _origSetFov;
        private static GetFovDelegate _origGetFov;
        private static SetFovDelegate _hookSetFovDel;
        private static GetFovDelegate _hookGetFovDel;

        private static Il2CppStringCharsDelegate _il2cppStringChars;
        private static GetBoolPropDelegate _camGetOrthographic;
        private static GetPtrPropDelegate _camGetTargetTexture;
        private static GetPtrPropDelegate _compGetGameObject;
        private static GetPtrPropDelegate _compGetTransform;
        private static GetPtrPropDelegate _transGetParent;
        private static GetPtrPropDelegate _objGetName;
        private static GetGameObjectSceneInjectedDelegate _goGetScene;
        private static GetSceneNameDelegate _sceneGetName;

        private static string _gameDir;
        private static string _iniPath;
        private static string _logPath;
        private static DateTime _lastIniWriteTime;
        private static long _lastIniCheckTick;

        public static bool Enabled = true;
        public static float GameplayMultiplier = 1.20f;
        public static float MinBaseFov = 35.0f;
        public static float MaxBaseFov = 85.0f;
        public static bool LogCameraNames = false;

        public static string[] IncludeTokens =
        {
            "Main Camera", "FieldScene", "Field", "Explore"
        };

        public static string[] ExcludeTokens =
        {
            "Battle", "Btl", "Combat", "Cutscene", "Event", "Evt", "Timeline", "Demo",
            "Movie", "Synthesis", "Mix", "Alchemy", "Shop", "Menu", "UI", "CharaView",
            "Portrait", "Photo", "Title", "Result"
        };

        private static readonly string[] SetFovNames =
        {
            "UnityEngine.Camera::set_fieldOfView",
            "UnityEngine.Camera::set_fieldOfView(System.Single)"
        };

        private static readonly string[] GetFovNames =
        {
            "UnityEngine.Camera::get_fieldOfView",
            "UnityEngine.Camera::get_fieldOfView()"
        };

        private static readonly ConcurrentDictionary<IntPtr, float> UnscaledBaseFov = new ConcurrentDictionary<IntPtr, float>();
        private static readonly HashSet<string> LoggedContexts = new HashSet<string>();
        private static readonly object LogLock = new object();

        [ThreadStatic]
        private static bool _inHookInspection;

        public static void Initialize()
        {
            try
            {
                _gameDir = AppDomain.CurrentDomain.BaseDirectory;
                _iniPath = Path.Combine(_gameDir, "AtelierReslerianaRWFix.ini");
                _logPath = Path.Combine(_gameDir, "AtelierReslerianaRWFix.log");

                File.WriteAllText(_logPath, "[AtelierReslerianaRWFix] Initializing (" + DateTime.Now + ")..." + Environment.NewLine);

                LoadOrCreateIni();

                IntPtr hDobby = LoadLibraryW(Path.Combine(_gameDir, "dobby.dll"));
                if (hDobby == IntPtr.Zero)
                {
                    LogError("Failed to load dobby.dll!");
                    return;
                }

                _hookSetFovDel = Hook_CameraSetFov;
                _hookGetFovDel = Hook_CameraGetFov;

                Thread t = new Thread(HookerThread);
                t.IsBackground = true;
                t.Name = "AtelierReslerianaRWFix.Hooker";
                t.Start();

                LogInfo("Hooker thread started. Waiting for GameAssembly.dll and the IL2CPP domain...");
            }
            catch (Exception ex)
            {
                LogError("Exception in Initialize: " + ex);
            }
        }

        private static void HookerThread()
        {
            try
            {
                IntPtr hGameAssembly = IntPtr.Zero;
                for (int i = 0; i < 600 && hGameAssembly == IntPtr.Zero; i++)
                {
                    hGameAssembly = GetModuleHandleW("GameAssembly.dll");
                    if (hGameAssembly == IntPtr.Zero) Thread.Sleep(100);
                }
                if (hGameAssembly == IntPtr.Zero)
                {
                    LogError("GameAssembly.dll never appeared in the process.");
                    return;
                }
                LogInfo("GameAssembly.dll found.");

                IntPtr pResolve = GetProcAddress(hGameAssembly, "il2cpp_resolve_icall");
                IntPtr pDomainGet = GetProcAddress(hGameAssembly, "il2cpp_domain_get");
                IntPtr pStrChars = GetProcAddress(hGameAssembly, "il2cpp_string_chars");

                if (pResolve == IntPtr.Zero)
                {
                    LogError("il2cpp_resolve_icall export not found in GameAssembly.dll.");
                    return;
                }

                ResolveIcallDelegate resolve = Marshal.GetDelegateForFunctionPointer<ResolveIcallDelegate>(pResolve);
                DomainGetDelegate domainGet = pDomainGet != IntPtr.Zero
                    ? Marshal.GetDelegateForFunctionPointer<DomainGetDelegate>(pDomainGet)
                    : null;

                if (pStrChars != IntPtr.Zero)
                    _il2cppStringChars = Marshal.GetDelegateForFunctionPointer<Il2CppStringCharsDelegate>(pStrChars);

                bool domainReady = false;
                for (int i = 0; i < 1500; i++)
                {
                    if (domainGet == null || domainGet() != IntPtr.Zero) { domainReady = true; break; }
                    Thread.Sleep(200);
                }
                if (!domainReady)
                {
                    LogError("IL2CPP domain never became ready.");
                    return;
                }

                LogInfo("IL2CPP domain is ready. Waiting 2 seconds for Unity to register its internal calls...");
                Thread.Sleep(2000);

                ResolveInspectionIcalls(resolve);

                for (int attempt = 1; attempt <= 60; attempt++)
                {
                    if (_origSetFov == null) TryHookSet(resolve);
                    if (_origGetFov == null) TryHookGet(resolve);

                    if (_origSetFov != null) break;
                    Thread.Sleep(1000);
                }

                if (_origSetFov == null)
                {
                    LogError("Could not resolve UnityEngine.Camera::set_fieldOfView. Nothing was hooked.");
                    return;
                }

                LogInfo("Active Filter -> Include=[" + string.Join(", ", IncludeTokens) + "]");
                LogInfo("Active Filter -> Exclude=[" + string.Join(", ", ExcludeTokens) + "]");
            }
            catch (Exception ex)
            {
                LogError("Exception in HookerThread: " + ex);
            }
        }

        private static void ResolveInspectionIcalls(ResolveIcallDelegate resolve)
        {
            _camGetOrthographic = TryResolveDelegate<GetBoolPropDelegate>(resolve,
                "UnityEngine.Camera::get_orthographic",
                "UnityEngine.Camera::get_orthographic()");

            _camGetTargetTexture = TryResolveDelegate<GetPtrPropDelegate>(resolve,
                "UnityEngine.Camera::get_targetTexture",
                "UnityEngine.Camera::get_targetTexture()");

            _compGetGameObject = TryResolveDelegate<GetPtrPropDelegate>(resolve,
                "UnityEngine.Component::get_gameObject",
                "UnityEngine.Component::get_gameObject()");

            _compGetTransform = TryResolveDelegate<GetPtrPropDelegate>(resolve,
                "UnityEngine.Component::get_transform",
                "UnityEngine.Component::get_transform()");

            _transGetParent = TryResolveDelegate<GetPtrPropDelegate>(resolve,
                "UnityEngine.Transform::GetParent",
                "UnityEngine.Transform::GetParent(UnityEngine.Transform)",
                "UnityEngine.Transform::get_parent");

            _objGetName = TryResolveDelegate<GetPtrPropDelegate>(resolve,
                "UnityEngine.Object::GetName(UnityEngine.Object)",
                "UnityEngine.Object::GetName");

            _goGetScene = TryResolveDelegate<GetGameObjectSceneInjectedDelegate>(resolve,
                "UnityEngine.GameObject::get_scene_Injected(UnityEngine.SceneManagement.Scene&)",
                "UnityEngine.GameObject::get_scene_Injected");

            _sceneGetName = TryResolveDelegate<GetSceneNameDelegate>(resolve,
                "UnityEngine.SceneManagement.Scene::GetNameInternal(System.Int32)",
                "UnityEngine.SceneManagement.Scene::GetNameInternal");
        }

        private static T TryResolveDelegate<T>(ResolveIcallDelegate resolve, params string[] names) where T : class
        {
            string used;
            IntPtr ptr = ResolveAny(resolve, names, out used);
            if (ptr != IntPtr.Zero)
            {
                LogInfo("[RESOLVE] " + used + " -> 0x" + ptr.ToInt64().ToString("X"));
                return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
            }
            return null;
        }

        private static IntPtr ResolveAny(ResolveIcallDelegate resolve, string[] names, out string usedName)
        {
            foreach (string n in names)
            {
                IntPtr namePtr = Marshal.StringToHGlobalAnsi(n);
                IntPtr result;
                try { result = resolve(namePtr); }
                finally { Marshal.FreeHGlobal(namePtr); }

                if (result != IntPtr.Zero)
                {
                    usedName = n;
                    return result;
                }
            }
            usedName = null;
            return IntPtr.Zero;
        }

        private static void TryHookSet(ResolveIcallDelegate resolve)
        {
            string used;
            IntPtr addr = ResolveAny(resolve, SetFovNames, out used);
            if (addr == IntPtr.Zero) return;

            LogInfo("[RESOLVE] " + used + " -> 0x" + addr.ToInt64().ToString("X"));

            IntPtr orig;
            int status = DobbyHook(addr, Marshal.GetFunctionPointerForDelegate(_hookSetFovDel), out orig);
            if (status == 0 && orig != IntPtr.Zero)
            {
                _origSetFov = Marshal.GetDelegateForFunctionPointer<SetFovDelegate>(orig);
                LogInfo("[HOOK] set_fieldOfView hooked.");
            }
        }

        private static void TryHookGet(ResolveIcallDelegate resolve)
        {
            string used;
            IntPtr addr = ResolveAny(resolve, GetFovNames, out used);
            if (addr == IntPtr.Zero) return;

            LogInfo("[RESOLVE] " + used + " -> 0x" + addr.ToInt64().ToString("X"));

            IntPtr orig;
            int status = DobbyHook(addr, Marshal.GetFunctionPointerForDelegate(_hookGetFovDel), out orig);
            if (status == 0 && orig != IntPtr.Zero)
            {
                _origGetFov = Marshal.GetDelegateForFunctionPointer<GetFovDelegate>(orig);
                LogInfo("[HOOK] get_fieldOfView hooked.");
            }
        }

        // ------------------------------------------------------------------
        // Hooks
        // ------------------------------------------------------------------
        private static void Hook_CameraSetFov(IntPtr camPtr, float value)
        {
            SetFovDelegate orig = _origSetFov;
            if (orig == null) return;

            if (camPtr == IntPtr.Zero || _inHookInspection)
            {
                orig(camPtr, value);
                return;
            }

            CheckHotReloadIni();

            try
            {
                if (Enabled && value >= MinBaseFov && value <= MaxBaseFov && IsFieldExplorationCamera(camPtr, value))
                {
                    UnscaledBaseFov[camPtr] = value;
                    float scaled = Math.Clamp(value * GameplayMultiplier, 15.0f, 150.0f);
                    orig(camPtr, scaled);
                    return;
                }
                else
                {
                    UnscaledBaseFov.TryRemove(camPtr, out _);
                }
            }
            catch
            {
                // Fallback to unmodified value
            }

            orig(camPtr, value);
        }

        private static float Hook_CameraGetFov(IntPtr camPtr)
        {
            GetFovDelegate orig = _origGetFov;
            float actual = orig != null ? orig(camPtr) : 60.0f;

            if (!_inHookInspection && camPtr != IntPtr.Zero && UnscaledBaseFov.TryGetValue(camPtr, out float baseFov))
            {
                return baseFov;
            }
            return actual;
        }

        private static bool IsFieldExplorationCamera(IntPtr camPtr, float baseFov)
        {
            _inHookInspection = true;
            try
            {
                // 1. Skip 2D orthographic & RenderTexture (UI/portrait) cameras
                if (_camGetOrthographic != null && _camGetOrthographic(camPtr) != 0)
                    return false;

                if (_camGetTargetTexture != null && _camGetTargetTexture(camPtr) != IntPtr.Zero)
                    return false;

                // 2. Get ONLY this Camera's own Hierarchy Path and its own owning Scene
                string hierarchy = GetCameraHierarchyPath(camPtr);
                string ownScene = GetCameraOwnSceneName(camPtr);

                string descriptor = string.IsNullOrEmpty(ownScene)
                    ? "Path:[" + hierarchy + "]"
                    : "CamScene:[" + ownScene + "] | Path:[" + hierarchy + "]";

                // 3. Check ExcludeKeywords against this camera's descriptor ONLY
                bool excluded = false;
                string matchedExclude = null;
                foreach (string ex in ExcludeTokens)
                {
                    if (descriptor.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        excluded = true;
                        matchedExclude = ex;
                        break;
                    }
                }

                // 4. Check IncludeKeywords against this camera's descriptor
                bool included = IncludeTokens.Length == 0;
                string matchedInclude = IncludeTokens.Length == 0 ? "*" : null;
                if (!excluded && !included)
                {
                    foreach (string inc in IncludeTokens)
                    {
                        if (descriptor.IndexOf(inc, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            included = true;
                            matchedInclude = inc;
                            break;
                        }
                    }
                }

                bool allowScale = !excluded && included;

                if (LogCameraNames)
                {
                    string logKey = descriptor + "|FOV:" + Math.Round(baseFov);
                    bool isNew;
                    lock (LogLock)
                    {
                        isNew = LoggedContexts.Count < 500 && LoggedContexts.Add(logKey);
                    }
                    if (isNew)
                    {
                        string verdict = allowScale
                            ? "SCALED (matched '" + matchedInclude + "')"
                            : (excluded ? "SKIPPED (excluded by '" + matchedExclude + "')" : "SKIPPED (no IncludeKeyword match)");

                        LogInfo("[Camera Inspector] " + verdict + " | " + descriptor +
                                " | BaseFOV=" + baseFov.ToString("F2", CultureInfo.InvariantCulture));
                    }
                }

                return allowScale;
            }
            catch
            {
                return false;
            }
            finally
            {
                _inHookInspection = false;
            }
        }

        private static string GetCameraOwnSceneName(IntPtr camPtr)
        {
            if (_compGetGameObject == null || _goGetScene == null || _sceneGetName == null)
                return string.Empty;

            try
            {
                IntPtr go = _compGetGameObject(camPtr);
                if (go == IntPtr.Zero) return string.Empty;

                int sceneHandle;
                _goGetScene(go, out sceneHandle);
                if (sceneHandle == 0) return string.Empty;

                return ReadIl2CppString(_sceneGetName(sceneHandle));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetCameraHierarchyPath(IntPtr camPtr)
        {
            string camName = GetUnityObjectName(camPtr);
            if (_compGetTransform == null || _transGetParent == null)
                return camName;

            try
            {
                IntPtr tr = _compGetTransform(camPtr);
                if (tr == IntPtr.Zero) return camName;

                List<string> parts = new List<string>(8);
                IntPtr curr = tr;
                for (int depth = 0; depth < 10 && curr != IntPtr.Zero; depth++)
                {
                    string name = GetUnityObjectName(curr);
                    if (!string.IsNullOrEmpty(name))
                        parts.Add(name);
                    curr = _transGetParent(curr);
                }

                parts.Reverse();
                return parts.Count > 0 ? string.Join("/", parts) : camName;
            }
            catch
            {
                return camName;
            }
        }

        private static string GetUnityObjectName(IntPtr objPtr)
        {
            if (objPtr == IntPtr.Zero || _objGetName == null)
                return string.Empty;

            try
            {
                IntPtr target = objPtr;
                if (_compGetGameObject != null)
                {
                    IntPtr go = _compGetGameObject(objPtr);
                    if (go != IntPtr.Zero)
                        target = go;
                }

                return ReadIl2CppString(_objGetName(target));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadIl2CppString(IntPtr il2cppStr)
        {
            if (il2cppStr == IntPtr.Zero) return string.Empty;
            try
            {
                if (_il2cppStringChars != null)
                {
                    IntPtr chars = _il2cppStringChars(il2cppStr);
                    return chars != IntPtr.Zero ? (Marshal.PtrToStringUni(chars) ?? string.Empty) : string.Empty;
                }
                int len = Marshal.ReadInt32(il2cppStr, 0x10);
                if (len <= 0 || len > 512) return string.Empty;
                return Marshal.PtrToStringUni(IntPtr.Add(il2cppStr, 0x14), len) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // ------------------------------------------------------------------
        // INI + Hot-Reload
        // ------------------------------------------------------------------
        private static void LoadOrCreateIni()
        {
            if (!File.Exists(_iniPath))
            {
                string defaultIni =
@"[FOV]
; Enable the Field/Exploration FOV multiplier (true / false)
Enabled = true

; Field/Exploration FOV multiplier
; Default = 1.00 | Minimum = 0.50 | Maximum = 2.50
; Note: Any value set above 2.50 is automatically capped at 2.50 to prevent camera projection distortion.
GameplayMultiplier = 1.20

[Filter]
; Comma-separated keywords. Matches the Field camera ('Main Camera' in FieldScene).
IncludeKeywords = Main Camera,FieldScene,Field,Explore

; Comma-separated keywords. Excludes Battle ('Battle Main Camera'), Cutscenes ('EventCamera'), and Menus.
ExcludeKeywords = Battle,Btl,Combat,Cutscene,Event,Evt,Timeline,Demo,Movie,Synthesis,Mix,Alchemy,Shop,Menu,UI,CharaView,Portrait,Photo,Title,Result

; Ignore narrow cutscene zoom lenses below MinBaseFOV or wide lenses above MaxBaseFOV
MinBaseFOV = 35.0
MaxBaseFOV = 85.0

[Debug]
; Log detected 3D camera names to AtelierReslerianaRWFix.log (set to true only when debugging cameras)
LogCameraNames = false
";
                File.WriteAllText(_iniPath, defaultIni);
            }
            ParseIniFile();
            _lastIniWriteTime = File.GetLastWriteTimeUtc(_iniPath);
        }

        private static void CheckHotReloadIni()
        {
            long now = Environment.TickCount64;
            if (now - _lastIniCheckTick < 1000) return;
            _lastIniCheckTick = now;

            try
            {
                if (!File.Exists(_iniPath)) return;
                DateTime writeTime = File.GetLastWriteTimeUtc(_iniPath);
                if (writeTime != _lastIniWriteTime)
                {
                    _lastIniWriteTime = writeTime;
                    ParseIniFile();
                    lock (LogLock) { LoggedContexts.Clear(); }
                    LogInfo("[Hot-Reload] Enabled=" + Enabled +
                            " Multiplier=" + GameplayMultiplier.ToString("F2", CultureInfo.InvariantCulture) +
                            " (Max=2.50)" +
                            " Include=[" + string.Join(",", IncludeTokens) + "]" +
                            " Exclude=[" + string.Join(",", ExcludeTokens) + "]");

                    // Immediately apply updated multiplier to active cached field cameras
                    _inHookInspection = true;
                    try
                    {
                        foreach (var kvp in UnscaledBaseFov)
                        {
                            float targetFov = Enabled
                                ? Math.Clamp(kvp.Value * GameplayMultiplier, 15.0f, 150.0f)
                                : kvp.Value;
                            _origSetFov?.Invoke(kvp.Key, targetFov);
                        }
                    }
                    finally
                    {
                        _inHookInspection = false;
                    }
                }
            }
            catch { }
        }

        private static void ParseIniFile()
        {
            foreach (string rawLine in File.ReadAllLines(_iniPath))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("["))
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool en))
                    Enabled = en;
                else if (key.Equals("GameplayMultiplier", StringComparison.OrdinalIgnoreCase) &&
                         float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float mult))
                    GameplayMultiplier = Math.Clamp(mult, 0.5f, 2.5f);
                else if (key.Equals("MinBaseFOV", StringComparison.OrdinalIgnoreCase) &&
                         float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float mn))
                    MinBaseFov = Math.Clamp(mn, 1.0f, 170.0f);
                else if (key.Equals("MaxBaseFOV", StringComparison.OrdinalIgnoreCase) &&
                         float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float mx))
                    MaxBaseFov = Math.Clamp(mx, 1.0f, 170.0f);
                else if (key.Equals("LogCameraNames", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool logCam))
                    LogCameraNames = logCam;
                else if (key.Equals("IncludeKeywords", StringComparison.OrdinalIgnoreCase))
                    IncludeTokens = SplitCsv(val);
                else if (key.Equals("ExcludeKeywords", StringComparison.OrdinalIgnoreCase))
                    ExcludeTokens = SplitCsv(val);
            }
        }

        private static string[] SplitCsv(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                parts[i] = parts[i].Trim();
            return parts;
        }

        private static void LogInfo(string msg)
        {
            lock (LogLock) { File.AppendAllText(_logPath, "[INFO]  " + msg + Environment.NewLine); }
        }

        private static void LogError(string msg)
        {
            lock (LogLock) { File.AppendAllText(_logPath, "[ERROR] " + msg + Environment.NewLine); }
        }
    }
}