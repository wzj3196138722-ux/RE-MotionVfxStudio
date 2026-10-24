using System.Numerics;

namespace VfxStudio.App.Camera;

public class OrbitCamera
{
    public enum StandardView { Front, Back, Right, Left, Top, Bottom }
    public bool Orthographic { get; private set; }
    public float OrthoHeight { get; private set; } = 2.8f;
    public Vector3 Target { get; set; } = new(0f, 1.15f, 0f);
    public float Yaw { get; set; } = 0.0f;
    public float Pitch { get; set; } = 0.12f;
    public float Distance { get; set; } = 2.2f;
    public float Fov { get; set; } = 45f * MathF.PI / 180f;

    public const float MinDistance = 0.3f;
    public const float MaxDistance = 25f;
    public const float MinPitch = -MathF.PI / 2f;
    public const float MaxPitch = MathF.PI / 2f;

    public Vector3 Position
    {
        get
        {
            float x = MathF.Cos(Pitch) * MathF.Sin(Yaw);
            float y = MathF.Sin(Pitch);
            float z = MathF.Cos(Pitch) * MathF.Cos(Yaw);
            return Target + new Vector3(x, y, z) * Distance;
        }
    }

    public Matrix4x4 GetViewMatrix()
    {
        // At the poles UnitY is parallel to the view direction. Use a stable
        // alternative so exact top and bottom views remain valid.
        Vector3 up = MathF.Abs(MathF.Cos(Pitch)) < 0.0001f ? -Vector3.UnitZ : Vector3.UnitY;
        return Matrix4x4.CreateLookAt(Position, Target, up);
    }

    public Matrix4x4 GetProjectionMatrix(float aspect, float near = 0.05f, float far = 100f)
    {
        aspect = Math.Max(aspect, 0.01f);
        return Orthographic
            ? Matrix4x4.CreateOrthographic(OrthoHeight * aspect, OrthoHeight, near, far)
            : Matrix4x4.CreatePerspectiveFieldOfView(Fov, aspect, near, far);
    }

    public void Rotate(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, MinPitch, MaxPitch);
    }

    public void Pan(float deltaX, float deltaY)
    {
        Matrix4x4 view = GetViewMatrix();
        Matrix4x4.Invert(view, out var inv);
        Vector3 right = new(inv.M11, inv.M12, inv.M13);
        Vector3 up = new(inv.M21, inv.M22, inv.M23);

        float speed = (Orthographic ? OrthoHeight : Distance) * 0.0015f;
        Target -= (right * deltaX - up * deltaY) * speed;
    }

    public void Zoom(float delta)
    {
        float factor = MathF.Pow(1.15f, -delta);
        if (Orthographic) OrthoHeight = Math.Clamp(OrthoHeight * factor, 0.2f, 50f);
        else Distance = Math.Clamp(Distance * factor, MinDistance, MaxDistance);
    }

    public void SetStandardView(StandardView view)
    {
        (Yaw, Pitch) = view switch
        {
            StandardView.Front => (0f, 0f),
            StandardView.Back => (MathF.PI, 0f),
            StandardView.Right => (MathF.PI / 2f, 0f),
            StandardView.Left => (-MathF.PI / 2f, 0f),
            StandardView.Top => (0f, MathF.PI / 2f),
            _ => (0f, -MathF.PI / 2f),
        };
        Orthographic = true;
    }

    public void ToggleProjection()
    {
        if (Orthographic) Distance = Math.Clamp(OrthoHeight / (2f * MathF.Tan(Fov / 2f)), MinDistance, MaxDistance);
        else OrthoHeight = Math.Clamp(2f * Distance * MathF.Tan(Fov / 2f), 0.2f, 50f);
        Orthographic = !Orthographic;
    }

    public void Focus(Vector3 center, float radius, float aspect = 1f)
    {
        Target = center;
        radius = Math.Max(radius, 0.05f);
        float verticalFov = Math.Min(Fov, 2f * MathF.Atan(MathF.Tan(Fov / 2f) * Math.Max(aspect, 0.01f)));
        Distance = Math.Clamp(radius * 1.3f / MathF.Sin(verticalFov / 2f), MinDistance, MaxDistance);
        OrthoHeight = Math.Clamp(radius * 2.6f, 0.2f, 50f);
    }

    public void Reset()
    {
        Target = new Vector3(0f, 1.15f, 0f);
        Yaw = 0.0f;
        Pitch = 0.12f;
        Distance = 2.2f;
        OrthoHeight = 2.8f;
        Orthographic = false;
    }
}
