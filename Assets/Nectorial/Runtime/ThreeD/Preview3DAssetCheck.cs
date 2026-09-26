using System.Collections.Generic;

namespace Nectorial.SlideEscape.Unity
{
    // Narrow public surface for editor build tooling; the board and mesh library stay internal.
    public static class Preview3DAssetCheck
    {
        public const string ProductName = Board3DView.ProductName;

        // Runs the same mesh-contract check as the runtime board and returns one description line per required mesh.
        public static bool TryValidate(out List<string> report, out string error)
        {
            report = new List<string>();
            Preview3DMeshLibrary library;
            if (!Preview3DMeshLibrary.TryLoad(out library, out error)) return false;
            foreach (string name in Preview3DMeshLibrary.RequiredNames)
                report.Add(name + " " + Preview3DMeshLibrary.Describe(library[name]));
            return true;
        }
    }
}
