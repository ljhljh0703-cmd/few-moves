# Third-Party Notices

This file records third-party dependencies referenced by the project. It does
not grant a license for the project's original source code.

## Apps in Toss Unity SDK

- Package: `im.toss.apps-in-toss-unity-sdk` 3.2.0
- Source: https://github.com/toss/apps-in-toss-unity-sdk
- Pinned revision: `f344f6facc3eeae848adb56630aa36597bd91be9`

The inspected revision does not contain a repository-level license or notice,
and its `package.json` does not declare a license. This repository therefore
references the SDK through Unity Package Manager and does not redistribute the
SDK's copied `AITTemplate` files. The original project-specific hook contents
are kept separately under `integrations/toss/` for review and integration.

## Newtonsoft Json for Unity

- Resolved package: `com.unity.nuget.newtonsoft-json` 3.2.2
- Source: Unity Package Manager

The installed package declares the Unity Companion License for the Unity
package and includes MIT notices for Newtonsoft.Json, Json.Net.Unity3D,
Newtonsoft.Json-for-Unity, and com.newtonsoft.json. Package source is fetched by
Unity Package Manager and is not copied into this repository.

## vConsole

The inspected Apps in Toss template contains a vConsole binary and an MIT
license notice from Tencent. Neither the binary nor the copied SDK template is
included in this repository. If vConsole is added later, its accompanying
license notice must be included with it.
