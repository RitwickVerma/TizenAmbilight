using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TizenAmbilight
{
    /// <summary>Outcome of one dc_request_capture_to_file_sync call, plus what we found on disk.</summary>
    public sealed class CaptureResult
    {
        public int Rc;                 // the library's return: 0 = wrote a file, 1 = no frame (screen off), -1 = validation
        public int RetWidth;           // +0x20, OUT
        public int RetHeight;          // +0x24, OUT
        public string RetPath = "";    // +0x28, OUT — absolute path the library claims it wrote
        public bool FileExists;        // does RetPath actually exist right now?
        public long FileBytes = -1;    // its size, or -1
        public string Error;           // managed-side failure (allocation/read-back), if any

        /// <summary>A usable frame means rc==0, a non-empty ret_path, and the file is on disk
        /// with bytes in it. rc==1 with an empty ret_path is the documented screen-off case and
        /// is NOT an error — it just yields no frame.</summary>
        public bool HasFrame
        {
            get
            {
                return Error == null && Rc == 0 && !string.IsNullOrEmpty(RetPath) &&
                       FileExists && FileBytes > 0;
            }
        }

        public string Status
        {
            get
            {
                if (Error != null) return "managed_error:" + Error;
                if (Rc != 0) return "rc=" + Rc + (string.IsNullOrEmpty(RetPath) ? " (no frame, ret_path empty)" : "");
                if (string.IsNullOrEmpty(RetPath)) return "rc=0 but ret_path empty";
                if (!FileExists) return "rc=0 but file missing: " + RetPath;
                if (FileBytes <= 0) return "rc=0 but file empty: " + RetPath;
                return "ok " + RetWidth + "x" + RetHeight + " " + FileBytes + "B";
            }
        }
    }

    /// <summary>
    /// The ONLY native entry point this app calls: dc_request_capture_to_file_sync() from
    /// libdisplay-capture-api.so.0.0, reached through dlopen/dlsym + a Cdecl delegate.
    ///
    /// Struct _dc_capt_req_data is 296 bytes (0x128); every offset below was recovered from the
    /// library's own disassembly:
    ///     +0x00 u32  app_type      (validated &lt;= 6)   -> 4
    ///     +0x04 u32  capture_mode  (validated &lt;= 4)   -> 0
    ///     +0x08 u32  compress_type (0 = PNG)          -> 0
    ///     +0x0c u32  jpeg_quality                      -> 90
    ///     +0x10 char* dir_path      (access(dir,W_OK))
    ///     +0x14 char* file_name
    ///     +0x18 int  width   (in/out, clamped in place)
    ///     +0x1c int  height  (in/out, clamped in place)
    ///     +0x20 int  ret_width  (OUT)
    ///     +0x24 int  ret_height (OUT)
    ///     +0x28 char[256] ret_path (OUT)
    ///
    /// A malformed struct returns a clean -1 (three enum range checks, two null-string checks,
    /// then access(W_OK)) — it cannot crash the TV. NO /dev node is opened by this file; the
    /// library opens whatever it needs internally.
    /// </summary>
    public static class CaptureApi
    {
        public const string LibName = "libdisplay-capture-api.so.0.0";
        public const string Symbol = "dc_request_capture_to_file_sync";

        // ---- _dc_capt_req_data ----
        const int ReqSize = 296;
        const int OffAppType = 0x00;
        const int OffMode = 0x04;
        const int OffCompress = 0x08;
        const int OffQuality = 0x0c;
        const int OffDir = 0x10;
        const int OffName = 0x14;
        const int OffWidth = 0x18;
        const int OffHeight = 0x1c;
        const int OffRetWidth = 0x20;
        const int OffRetHeight = 0x24;
        const int OffRetPath = 0x28;
        const int RetPathLen = 256;

        // ---- field values proven on hardware (QN77S84FAEXZA, Tizen 9) ----
        public const int AppType = 4;
        public const int CaptureMode = 0;
        public const int CompressType = 0;   // PNG
        public const int JpegQuality = 90;
        public const int CompressPng = 0;
        public const int CompressJpeg = 1;

        // ---- the defaults the main loop requests ----
        // 640x360 + PNG is the shipped default because the PNG decoder is on-device verified and
        // that pair is the one with a recorded end-to-end number (~8.9 fps). The requested size IS
        // a lever (1280x720 measured ~171 ms capture, 640x360 ~89 ms) and the library floors its
        // output at 480x270. compress_type=1 (JPEG) is much cheaper in the CAPTURE CALL alone
        // (~12 ms, ~15x), but end to end the saving is far smaller (~2.6x, measured), so it stays
        // opt-in via the compressType preference until that gap is addressed.
        public const int RequestWidth = 640;
        public const int RequestHeight = 360;

        const int RTLD_LAZY = 0x00001;
        const int RTLD_NOW = 0x00002;

        [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
        static extern IntPtr dlopen(string filename, int flags);

        [DllImport("libdl.so.2", EntryPoint = "dlsym", CharSet = CharSet.Ansi)]
        static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl.so.2", EntryPoint = "dlerror")]
        static extern IntPtr dlerror();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int CaptureToFileFn(IntPtr req);

        static IntPtr _fn = IntPtr.Zero;
        static string _resolvedPath = "<unresolved>";
        // The LAST failure, kept for the diagnostic message Capture() reports. It is deliberately
        // NOT used to short-circuit: a failed init must not poison later attempts, because the
        // library can become available after the app has started.
        static string _initError;

        /// <summary>Resolve the library and the symbol. Caches SUCCESS only — a failed init is
        /// re-attempted on every call, because the app idles and retries until the device is ready.
        /// Returns null on success or an error string. Safe to call repeatedly.</summary>
        public static string Init()
        {
            if (_fn != IntPtr.Zero) return null;

            string[] dirs = { "", "/prd/usr/lib/", "/usr/lib/", "/usr/lib64/" };
            IntPtr handle = IntPtr.Zero;
            var errs = new StringBuilder();

            foreach (string d in dirs)
            {
                string path = d + LibName;
                try
                {
                    TakeError();
                    IntPtr h = dlopen(path, RTLD_NOW);
                    string e = TakeError();
                    if (h == IntPtr.Zero) h = dlopen(path, RTLD_LAZY);
                    if (h != IntPtr.Zero) { handle = h; _resolvedPath = path; break; }
                    errs.Append(path).Append(": ").Append(e).Append("; ");
                }
                catch (Exception ex)
                {
                    errs.Append(path).Append(": ").Append(ex.GetType().Name).Append("; ");
                }
            }

            if (handle == IntPtr.Zero)
            {
                _initError = "dlopen(" + LibName + ") failed: " + errs;
                return _initError;
            }

            TakeError();
            IntPtr sym = dlsym(handle, Symbol);
            string derr = TakeError();
            if (sym == IntPtr.Zero)
            {
                _initError = "dlsym(" + Symbol + ") failed in " + _resolvedPath + ": " + derr;
                return _initError;
            }

            _fn = sym;   // passed verbatim; the Thumb bit is preserved
            _initError = null;
            return null;
        }

        public static IntPtr FunctionPointer { get { return _fn; } }
        public static string ResolvedPath { get { return _resolvedPath; } }
        public static bool Ready { get { return _fn != IntPtr.Zero; } }

        static string TakeError()
        {
            IntPtr p = dlerror();
            if (p == IntPtr.Zero) return "";
            string s = Marshal.PtrToStringAnsi(p);
            return s ?? "";
        }

        /// <summary>Make ONE capture call: compress_type and jpeg quality are parameters so the
        /// preference can steer the call, and so compress_type 0 (PNG) can be compared against 1
        /// (JPEG) through this same native entry point. This is the ONLY entry point used. Every
        /// OUT field the library writes is read back before the buffers are freed, even when rc is
        /// negative. Never throws.</summary>
        public static CaptureResult Capture(string dirPath, string fileName, int width, int height,
                                            int compressType, int jpegQuality)
        {
            var res = new CaptureResult();
            IntPtr req = IntPtr.Zero, dstr = IntPtr.Zero, nstr = IntPtr.Zero;

            if (_fn == IntPtr.Zero)
            {
                res.Rc = -1000;
                res.Error = "capture library not resolved (" + (_initError ?? "Init not called") + ")";
                return res;
            }

            try
            {
                req = Marshal.AllocHGlobal(ReqSize);
                if (req == IntPtr.Zero) { res.Error = "AllocHGlobal(req) returned null"; return res; }
                for (int i = 0; i < ReqSize; i++) Marshal.WriteByte(req, i, 0);

                dstr = Marshal.StringToHGlobalAnsi(dirPath);
                nstr = Marshal.StringToHGlobalAnsi(fileName);
                if (dstr == IntPtr.Zero || nstr == IntPtr.Zero)
                {
                    res.Error = "StringToHGlobalAnsi returned null";
                    return res;
                }

                Marshal.WriteInt32(req, OffAppType, AppType);
                Marshal.WriteInt32(req, OffMode, CaptureMode);
                Marshal.WriteInt32(req, OffCompress, compressType);
                Marshal.WriteInt32(req, OffQuality, jpegQuality);
                Marshal.WriteIntPtr(req, OffDir, dstr);
                Marshal.WriteIntPtr(req, OffName, nstr);
                Marshal.WriteInt32(req, OffWidth, width);
                Marshal.WriteInt32(req, OffHeight, height);

                CaptureToFileFn fn =
                    (CaptureToFileFn)Marshal.GetDelegateForFunctionPointer(_fn, typeof(CaptureToFileFn));
                res.Rc = fn(req);

                // ---- read every OUT field back, before the free ----
                res.RetWidth = Marshal.ReadInt32(req, OffRetWidth);
                res.RetHeight = Marshal.ReadInt32(req, OffRetHeight);

                byte[] rp = new byte[RetPathLen];
                // NOTE: Marshal.Copy's 3rd argument is an offset into the DESTINATION array, not
                // the source pointer. Add the struct offset to the pointer instead.
                Marshal.Copy(IntPtr.Add(req, OffRetPath), rp, 0, RetPathLen);
                res.RetPath = ReadCStr(rp);
            }
            catch (Exception e)
            {
                res.Error = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (nstr != IntPtr.Zero) { try { Marshal.FreeHGlobal(nstr); } catch { } }
                if (dstr != IntPtr.Zero) { try { Marshal.FreeHGlobal(dstr); } catch { } }
                if (req != IntPtr.Zero) { try { Marshal.FreeHGlobal(req); } catch { } }
            }

            // ---- did a real file land? ----
            if (res.Error == null && res.Rc == 0 && !string.IsNullOrEmpty(res.RetPath))
            {
                try
                {
                    res.FileExists = File.Exists(res.RetPath);
                    if (res.FileExists) res.FileBytes = new FileInfo(res.RetPath).Length;
                }
                catch (Exception e)
                {
                    res.Error = "stat ret_path: " + e.GetType().Name + ": " + e.Message;
                }
            }
            return res;
        }

        static string ReadCStr(byte[] b)
        {
            int n = 0;
            while (n < b.Length && b[n] != 0) n++;
            return Encoding.UTF8.GetString(b, 0, n);
        }

        // ================================================================ writable-dir discovery

        /// <summary>Ordered candidate directories for the capture output, most likely first: the
        /// app's OWN data directory (Tizen.Application DirectoryInfo.Data), then this app's
        /// deterministic paths under the two SDK apps roots, then /tmp. Every path either contains
        /// THIS app's id or is the shared /tmp — none of them is the probe's directory.</summary>
        public static List<string> CandidateDirs(out Dictionary<string, object> discovery)
        {
            var d = new Dictionary<string, object>();
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            string appData = null;
            try
            {
                Tizen.Applications.Application app = Tizen.Applications.Application.Current;
                d["application_current_is_null"] = (app == null);
                if (app != null)
                {
                    try
                    {
                        Tizen.Applications.DirectoryInfo di = app.DirectoryInfo;
                        if (di != null) appData = di.Data;
                    }
                    catch (Exception e) { d["directory_info_error"] = e.GetType().Name + ": " + e.Message; }
                }
            }
            catch (Exception e) { d["application_current_error"] = e.GetType().Name + ": " + e.Message; }

            d["application_data_dir"] = appData ?? "";
            Add(list, seen, appData);
            Add(list, seen, "/opt/usr/apps/" + Ambilight.AppId + "/data");
            Add(list, seen, "/home/owner/apps_rw/" + Ambilight.AppId + "/data");
            Add(list, seen, "/tmp");

            d["ordered_candidates"] = ToObjList(list);
            discovery = d;
            return list;
        }

        static void Add(List<string> list, HashSet<string> seen, string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            string t = p.Trim();
            if (t.Length == 0 || t[0] != '/') return;
            if (seen.Add(t)) list.Add(t);
        }

        static List<object> ToObjList(IEnumerable<string> src)
        {
            var l = new List<object>();
            if (src != null) foreach (string s in src) l.Add(s);
            return l;
        }

        /// <summary>1-byte create/read/delete test — a .NET analogue of the library's own
        /// access(dir, W_OK). The library's check is authoritative; this only orders the
        /// candidates so the first call is the most likely to succeed.</summary>
        public static bool WriteTest(string dir, out string detail)
        {
            detail = "";
            string f = null;
            try
            {
                f = Path.Combine(dir, ".amb_wtest");
                File.WriteAllBytes(f, new byte[] { 0x37 });
                byte[] rb = File.ReadAllBytes(f);
                detail = "write_ok";
                return rb.Length == 1 && rb[0] == 0x37;
            }
            catch (Exception e)
            {
                detail = e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                if (f != null) { try { File.Delete(f); } catch { } }
            }
        }
    }
}
