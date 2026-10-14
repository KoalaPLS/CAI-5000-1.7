using System.Runtime.CompilerServices;

// Compile-time bridge for historical IMGUI assembly identities referenced by
// RimWorld 1.7 Assembly-CSharp metadata. Waylith provides the real types via
// UnityStubs.dll at runtime.
[assembly: TypeForwardedTo(typeof(UnityEngine.GUIContent))]
[assembly: TypeForwardedTo(typeof(UnityEngine.GUIStyle))]
