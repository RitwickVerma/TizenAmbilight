/* Stand-in for /usr/lib/libtzcapturec.so, which a sideloaded app may not open (EPERM on Tizen 8).
 *
 * libvideo-capture-impl-sec.so (the full-frame capture implementation) lists libtzcapturec.so as
 * DT_NEEDED but calls TZCAPTURE_* only from scaler_capture_secure, the protected-content branch.
 * The app loads this stub RTLD_GLOBAL before impl-sec, so the dynamic linker resolves the
 * DT_NEEDED entry by soname to this object. Every call fails, so protected content stays
 * uncapturable and the app's DRM fallback to the sampler still applies.
 *
 * Build: native/build.sh (clang + lld, no Tizen SDK). Output goes to res/libtzcapturec.so, which
 * is committed because the .NET-only build cannot compile C. See VideoPlaneCapture.PreloadImpl.
 */
int TZCAPTURE_Initialize(void) { return -1; }
int TZCAPTURE_Capture_screen(void) { return -1; }
int TZCAPTURE_Finalize(void) { return -1; }
