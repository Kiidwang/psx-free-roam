using Godot;

public partial class PSXPostProcess : Node
{
    public override void _Ready()
    {
        // Screen pixelation
        var layer = new CanvasLayer { Layer = 127 };
        AddChild(layer);

        var rect = new ColorRect();
        rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        rect.MouseFilter = Control.MouseFilterEnum.Ignore;
        var screenMat = new ShaderMaterial();
        screenMat.Shader = GD.Load<Shader>("res://PSXScreenEffect.gdshader");
        rect.Material = screenMat;
        layer.AddChild(rect);

        // Vertex snap Ã¢â‚¬â€ only static world geometry, skips character models
        var snapMat = new ShaderMaterial();
        snapMat.Shader = GD.Load<Shader>("res://PSXVertexSnap.gdshader");
        ApplyVertexSnapToStatics(GetTree().Root, snapMat);
    }

    private static void ApplyVertexSnapToStatics(Node root, ShaderMaterial mat)
    {
        if (root is MeshInstance3D mesh && mesh.GetParent() is StaticBody3D)
            mesh.MaterialOverride = mat;
        foreach (Node child in root.GetChildren())
            ApplyVertexSnapToStatics(child, mat);
    }
}
