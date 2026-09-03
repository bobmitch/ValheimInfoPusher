using UnityEngine;
using ValheimRelay.Core.Qr;

namespace ValheimRelay.Plugin
{
    /// <summary>
    /// Turns a symbol from <see cref="QrCode"/> into something IMGUI can draw.
    /// </summary>
    internal static class QrTexture
    {
        /// <summary>
        /// The light border the specification requires. Without it the panel's
        /// dark backing runs straight into the symbol and no scanner will find
        /// the finder patterns, however sharp the modules are.
        /// </summary>
        private const int QuietZone = 4;

        /// <summary>
        /// Builds the symbol at a whole number of pixels per module, as close to
        /// <paramref name="targetPixels"/> across as that allows.
        /// <para>
        /// Scaling here rather than at draw time is what keeps every module the
        /// same width. Drawing a one-pixel-per-module texture into an arbitrary
        /// rectangle leaves the filtering to decide where the module edges fall,
        /// and a symbol whose modules are alternately two and three pixels wide
        /// is exactly the sort of thing that scans on the machine it was built
        /// on and nowhere else.
        /// </para>
        /// </summary>
        internal static Texture2D Create(QrCode qr, int targetPixels)
        {
            var modules = qr.Size + (QuietZone * 2);
            var scale = Mathf.Max(2, Mathf.RoundToInt(targetPixels / (float)modules));
            var pixels = modules * scale;

            var texture = new Texture2D(pixels, pixels, TextureFormat.RGB24, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,

                // Nothing owns this but the panel, and the panel destroys it.
                hideFlags = HideFlags.HideAndDontSave
            };

            var light = new Color32(255, 255, 255, 255);
            var dark = new Color32(0, 0, 0, 255);

            var colours = new Color32[pixels * pixels];
            for (var i = 0; i < colours.Length; i++) colours[i] = light;

            for (var y = 0; y < qr.Size; y++)
            {
                for (var x = 0; x < qr.Size; x++)
                {
                    if (!qr[x, y]) continue;

                    // A texture's origin is its bottom-left corner and a
                    // symbol's is its top-left, so the rows go in inverted. A
                    // symbol written the other way up still scans on readers
                    // that try the transpose, which is precisely why this is
                    // worth being deliberate about rather than checking by eye.
                    var left = (x + QuietZone) * scale;
                    var bottom = (modules - 1 - (y + QuietZone)) * scale;

                    for (var dy = 0; dy < scale; dy++)
                    {
                        var row = (bottom + dy) * pixels;
                        for (var dx = 0; dx < scale; dx++) colours[row + left + dx] = dark;
                    }
                }
            }

            texture.SetPixels32(colours);
            texture.Apply(false, false);
            return texture;
        }
    }
}
