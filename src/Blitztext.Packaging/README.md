# Blitztext Windows Packaging

This folder contains the MSIX/Desktop Bridge packaging scaffold for the Windows port.

The default `Blitztext.sln` intentionally excludes `Blitztext.Packaging.wapproj` so `dotnet build` and CI can run on machines without Visual Studio MSIX packaging targets. Build the package from a Visual Studio Developer PowerShell with the MSIX Packaging Tools installed:

```powershell
.\build-windows.ps1 -Configuration Release -Package
```

Before a Microsoft Store submission, replace the manifest identity and publisher with the reserved Partner Center identity. The manifest already declares the capabilities the app needs for the first Windows port: `internetClient`, `microphone`, and `runFullTrust`.
