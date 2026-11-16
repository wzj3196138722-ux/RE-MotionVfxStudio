using System.Numerics;

namespace VfxStudio.Re4Render;

/// <summary>Emitter motion for previewing effects that only form while the emitter moves (PolygonTrail
/// sweeps, which in the game follow a bone). Not part of the effect: the preview's emitter is otherwise still.</summary>
public static class PreviewMotion
{
    /// <summary>The emitter swings about the vertical axis through the origin on a 1 m arm, ±60° once per
    /// second (60 ticks). Row-major world matrices, one per tick (tick t uses element t − 1).</summary>
    public static Matrix4x4[] Swing(int ticks)
    {
        var worlds = new Matrix4x4[ticks];
        for (int i = 0; i < ticks; i++)
        {
            float angle = MathF.PI / 3 * MathF.Sin(2 * MathF.PI * (i + 1) / 60f);
            worlds[i] = Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateTranslation(MathF.Cos(angle), 0, -MathF.Sin(angle));
        }
        return worlds;
    }
}
