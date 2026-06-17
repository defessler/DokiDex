using System.Collections.Generic;
using System.Linq;

namespace DokiDex.Web;

// One blockout primitive: a box at a world position with a size (world units).
public sealed record Prim(double X, double Y, double Z, double Size);

// A blockout scene the "Scene" surface posts: output size + camera (on +Z looking at the origin) + primitives.
public sealed record BlockoutScene(int Width, int Height, double CameraDist, double Fov, List<Prim>? Prims);

// Scene-to-Image blockout, done HEADLESS: a pure software depth rasterizer. Each primitive is perspective-
// projected (nearer => larger + brighter) and z-buffer composited (nearer occludes farther), producing a
// grayscale DEPTH MAP — exactly what a Depth-ControlNet wants — with NO three.js, no WebGL, no display, no GPU.
// Pure + total => unit-tested (projection, perspective scale, occlusion). The SPA paints the returned bytes to
// a canvas and feeds it to the shipped ControlNet control-image path.
public static class Blockout
{
    // Render a W*H grayscale depth map (row-major, one byte per pixel; 0 = empty/far, 255 = nearest surface).
    public static byte[] RenderDepth(BlockoutScene s)
    {
        int w = System.Math.Clamp(s.Width, 1, 1024), h = System.Math.Clamp(s.Height, 1, 1024);
        var depth = new byte[w * h];                 // output (brightness)
        var zbuf = new double[w * h];                // nearest camera-distance seen per pixel (smaller = nearer)
        for (int i = 0; i < zbuf.Length; i++) zbuf[i] = double.MaxValue;

        var cam = s.CameraDist <= 0 ? 5.0 : s.CameraDist;
        var fov = s.Fov is > 0 and < 3.13 ? s.Fov : 1.0;       // radians
        double focal = (h / 2.0) / System.Math.Tan(fov / 2.0);
        // depth normalization band around the camera distance (nearer of the band = 255)
        double near = cam * 0.25, far = cam * 1.75;

        foreach (var p in (s.Prims ?? new()).OrderByDescending(p => p.Z))   // far-to-near so nearer overwrites
        {
            double zc = cam - p.Z;                  // distance from camera along the view axis
            if (zc <= 0.01) continue;               // behind / at the camera
            double sx = w / 2.0 + p.X * focal / zc;
            double sy = h / 2.0 - p.Y * focal / zc;
            double half = System.Math.Max(0.5, p.Size * focal / zc / 2.0);   // perspective half-extent in px
            byte val = (byte)System.Math.Clamp(255.0 * (far - zc) / (far - near), 0, 255);

            int x0 = (int)System.Math.Floor(sx - half), x1 = (int)System.Math.Ceiling(sx + half);
            int y0 = (int)System.Math.Floor(sy - half), y1 = (int)System.Math.Ceiling(sy + half);
            for (int y = System.Math.Max(0, y0); y < System.Math.Min(h, y1); y++)
                for (int x = System.Math.Max(0, x0); x < System.Math.Min(w, x1); x++)
                {
                    int idx = y * w + x;
                    if (zc < zbuf[idx]) { zbuf[idx] = zc; depth[idx] = val; }   // z-buffer: nearer wins
                }
        }
        return depth;
    }
}
