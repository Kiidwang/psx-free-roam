shader_type canvas_item;

uniform float pixel_width  : hint_range(160.0, 1280.0, 1.0) = 640.0;
uniform float pixel_height : hint_range(120.0,  960.0, 1.0) = 480.0;
uniform float color_depth  : hint_range(4.0,   256.0, 1.0) = 32.0;

uniform sampler2D screen_tex : hint_screen_texture, repeat_disable, filter_nearest;

void fragment() {
    vec2 res     = vec2(pixel_width, pixel_height);
    vec2 snapped = floor(UV * res) / res;
    vec4 color   = texture(screen_tex, snapped);
    COLOR = color;
}
