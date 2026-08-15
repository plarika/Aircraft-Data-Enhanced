# Exact SDR# SDK compatibility

Official target hosts are SDR# production revision 1921 and beta x86 revision 1922. Copy the official SDK DLLs
to `lib/`, run `PREPARAR_SDK_ESTAVEL.ps1`, select the matching host revision, then build. The approval file
records exact SHA-256, assembly version, file version and product version. CI stubs prove compile contracts only;
they are never a substitute for local testing with the official SDK.
