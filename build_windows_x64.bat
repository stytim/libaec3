@echo off
rem Builds the x64 Windows library into install\windows-x64 (bin\aec3.dll, lib\aec3.lib, include\aec3_api.h).
setlocal
set "ROOT=%~dp0"
set "BUILD_DIR=%ROOT%build\windows-x64"
cmake -S "%ROOT%." -B "%BUILD_DIR%" -A x64 -DBUILD_SHARED_LIBS=ON -DBUILD_WINDOWS=ON -DUSE_SIMD=OFF -DCMAKE_INSTALL_PREFIX="%ROOT%install\windows-x64" || exit /b 1
cmake --build "%BUILD_DIR%" --config Release --parallel || exit /b 1
cmake --install "%BUILD_DIR%" --config Release || exit /b 1
