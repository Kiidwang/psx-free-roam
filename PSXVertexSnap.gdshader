shader_type spatial;

uniform float snap_resolution : hint_range(32.0, 2048.0, 1.0) = 600.0;

void vertex() {
    vec4 clip = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    float w = clip.w;
    clip.xy = round(clip.xy / w * snap_resolution) / snap_resolution * w;
    POSITION = clip;
}

void fragment() {
    ALBEDO = vec3(1.0);
}
