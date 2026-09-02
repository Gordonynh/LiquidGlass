namespace ClassIsland.LiquidGlass.Rendering;

/// <summary>
/// 液态玻璃的 HLSL 像素着色器。
/// </summary>
/// <remarks>
/// 数学和 CPU 那份 <c>GlassRenderer</c> 一一对应，只是搬到了 GPU：
/// 圆角矩形 SDF → 解析梯度 → 四分之一圆倒角剖面 → 三维法线 →
/// Snell 折射采样桌面纹理 → 两盏对向光的洛伦兹窄带高光 → 内侧暗边 → 色散。
/// <para/>
/// 关键差别在于<b>桌面纹理本来就在显存里</b>（DXGI 桌面复制拿到的就是 D3D11 纹理），
/// 所以采样是免费的，不存在 CPU 那条路的两次跨显存搬运。
/// <para/>
/// ⚠ 着色器源码里<b>只能用 ASCII</b>。D3DCompile 按 ANSI 编组，
/// 中文注释会把行截断，报出来是最后一行「unexpected end of file」，
/// 和真正的语法错误长得一模一样。解释一律写在这里，不写进 HLSL。
/// </remarks>
internal static class GlassHlsl
{
    public const string Source = """
        cbuffer Params : register(b0)
        {
            float2 gBarSize;
            float2 gBarOriginUV;
            float2 gDesktopSize;
            float  gRadius;
            float  gThickness;
            float2 gLightDir;
            float  gLightIntensity;
            float  gAmbient;
            float  gChromatic;
            float  gRefractiveIndex;
            float  gBackgroundDistance;
            float  gTintAmount;
            float3 gTint;
            float  gLensing;
            float  gScaling;
            float  gIsDark;
            float  gPad;          // shadow margin around the pill, device px
            float  gShadowAlpha;  // driven by the backdrop
            float  gClarity;      // 1 = clear plate, 0 = frosted
        };

        Texture2D    gDesktop : register(t0);
        SamplerState gSampler : register(s0);

        struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

        // Fullscreen triangle, no vertex buffer needed.
        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.uv = uv;
            o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        // L^n norm. n = 4 gives a superellipse corner: zero curvature on the axes,
        // so the specular arc has no seam where the corner meets the straight edge.
        // n = 2 is an exact circle and puts a visible kink there.
        float lenN(float2 v)
        {
            float2 p4 = v * v * v * v;
            return pow(p4.x + p4.y, 0.25);
        }

        float sdRoundRect(float2 p, float2 b, float r)
        {
            float2 q = abs(p) - b + r;
            return min(max(q.x, q.y), 0.0) + lenN(max(q, 0.0)) - r;
        }

        // Rounding only subtracts a constant, so the gradient is the plain box gradient.
        // Corner branch uses the L^4 gradient: d(lenN)/dv = (v/d)^(n-1), which is NOT
        // unit length for n != 2, hence the normalize.
        float2 sdGradient(float2 p, float2 b, float r)
        {
            float2 s = float2(p.x < 0 ? -1 : 1, p.y < 0 ? -1 : 1);
            float2 q = abs(p) - b + r;
            if (q.x > 0 || q.y > 0)
            {
                float2 m = max(q, 0.0);
                float d = lenN(m);
                if (d < 1e-5)
                {
                    return float2(s.x, 0);
                }

                float2 t = m / d;
                float2 g = normalize(t * t * t);
                return s * g;
            }
            return q.x > q.y ? float2(s.x, 0) : float2(0, s.y);
        }

        // ---- Separable Gaussian, one axis per pass ----
        // The scatter layer: light spreading through the thickness of the slab.
        // Without it the refracted content is razor sharp, which reads as a
        // distortion filter rather than a material.
        cbuffer BlurParams : register(b1)
        {
            float2 gTexel;      // 1 / source size
            float2 gDir;        // (1,0) then (0,1)
            float  gSigma;      // device px
            float3 gBlurPad;
        };

        float4 PSBlur(VSOut i) : SV_TARGET
        {
            float sigma = max(gSigma, 0.01);
            int radius = min(24, (int)ceil(sigma * 2.5));
            float4 acc = 0;
            float sum = 0;

            [loop]
            for (int k = -radius; k <= radius; k++)
            {
                float wgt = exp(-0.5 * (k * k) / (sigma * sigma));
                acc += gDesktop.Sample(gSampler, i.uv + gDir * gTexel * (float)k) * wgt;
                sum += wgt;
            }

            return acc / max(sum, 1e-5);
        }

        // ---- Crop with edge extension ----
        // The bar sits at y = 0, so the crop rectangle ALWAYS hangs off the top of
        // the screen. Clearing the target and copying only the overlapping part
        // leaves transparent black outside, and both the blur and the rim-side
        // refraction then drag that black into the bar: a dark band along the top
        // edge, worst exactly where the lensing is strongest.
        // Sampling with a clamp sampler replicates the nearest real pixel instead.
        cbuffer CropParams : register(b2)
        {
            float2 gCropOrigin;   // crop top-left in desktop texels, may be negative
            float2 gCropSize;     // crop size in texels
            float2 gDeskSize;     // desktop texture size
            float2 gCropSpare;
        };

        float4 PSCrop(VSOut i) : SV_TARGET
        {
            float2 t = (gCropOrigin + i.uv * gCropSize) / gDeskSize;
            return gDesktop.SampleLevel(gSampler, t, 0);
        }

        // ---- Backdrop statistics ----
        // Writes (luma, luma^2). Generating mips averages them all the way down,
        // so the smallest mip holds E[x] and E[x^2] for the whole crop -- mean and
        // variance for free, entirely on the GPU, no readback.
        float4 PSStats(VSOut i) : SV_TARGET
        {
            float3 c = gDesktop.Sample(gSampler, i.uv).rgb;
            float l = dot(c, float3(0.299, 0.587, 0.114));
            return float4(l, l * l, 0, 0);
        }

        Texture2D gStats : register(t1);

        // The unblurred crop. Sampling ONLY the blurred one is what turns the
        // material into frosted acrylic: a flat plate does not diffuse, it
        // transmits. Diffusion belongs where the lens actually bends light.
        Texture2D gSharp : register(t2);

        float4 PSMain(VSOut i) : SV_TARGET
        {
            // The target is the pill plus a margin on every side, so the shadow
            // has somewhere to live. Pill coordinates are measured from its centre.
            float2 full = gBarSize + gPad * 2.0;
            float2 px = i.uv * full - gPad;
            float2 halfSize = gBarSize * 0.5;
            float2 p = px - halfSize;

            float radius = min(gRadius, min(halfSize.x, halfSize.y));

            // Ceiling, not just a floor: past min(w,h)/2 the flat interior disappears
            // and the whole element becomes bevel, i.e. a fisheye lens.
            float thickness = clamp(gThickness, 0.5, min(radius, min(halfSize.x, halfSize.y)));

            float sd = sdRoundRect(p, halfSize, radius);
            float coverage = saturate(0.5 - sd);

            // ---- Shadow ----
            // Two terms: a wide soft one for depth, and a 1 px contact shadow so the
            // bottom edge reads as touching something. A blurred mask of an SDF shape
            // is well approximated by a smoothstep across the distance, which costs
            // nothing -- a real Gaussian here would need dozens of taps.
            // Backdrop statistics from the smallest mip: one tap, no readback.
            float2 st = gStats.SampleLevel(gSampler, float2(0.5, 0.5), 20.0);
            float bgMean = saturate(st.x);
            float bgVar = max(st.y - st.x * st.x, 0.0);
            float bgRms = sqrt(bgVar);

            // Apple raises the shadow opacity over text and lowers it over a solid
            // light background. Busier backdrop -> heavier; brighter -> lighter.
            float shadowAlpha = (0.14 + 0.22 * saturate(bgRms / 0.18))
                              * (1.0 - 0.40 * bgMean)
                              * (gShadowAlpha / 0.20);

            float h = gBarSize.y;
            float softSigma = max(0.22 * h, 1.0);
            float softOff = 0.10 * h;
            float contactSigma = max(1.5 * gScaling, 1.0);
            float contactOff = 1.0 * gScaling;

            float sdSoft = sdRoundRect(p - float2(0.0, softOff), halfSize, radius);
            float sdContact = sdRoundRect(p - float2(0.0, contactOff), halfSize, radius);
            float soft = 1.0 - smoothstep(-softSigma, softSigma, sdSoft);
            float contact = 1.0 - smoothstep(-contactSigma, contactSigma, sdContact);

            // Only outside the glass: a shadow under an opaque body is invisible anyway,
            // and letting it bleed inward would muddy the refraction.
            float shadowA = saturate(shadowAlpha * soft + shadowAlpha * 0.5 * contact)
                          * (1.0 - coverage);

            if (coverage <= 0.0)
            {
                return float4(0, 0, 0, shadowA);
            }

            // Squircle bevel profile. w = 1 at the rim, 0 at depth >= thickness.
            // A quarter-circle profile leaves a visible seam where the dome meets the
            // flat interior; stretched into a wide short bar that seam is very obvious.
            // The w^3 numerator softens the flat-to-curve transition.
            float w = saturate((thickness + sd) / thickness);
            float w2 = w * w;
            float w3 = w2 * w;
            float u = 1.0 - w2 * w2;
            float den = max(sqrt(pow(max(u, 0.0), 1.5) + w3 * w3), 1e-5);
            float nCos = w3 / den;
            float nSin = pow(max(u, 0.0), 0.75) / den;

            float2 g = sdGradient(p, halfSize, radius);
            float3 n = normalize(float3(g * nCos, max(nSin, 1e-4)));

            // Snell refraction onto a virtual background plane.
            float glassHeight = thickness * nSin;
            float eta = 1.0 / max(gRefractiveIndex, 1.01);
            float3 incident = float3(0, 0, -1);
            float3 refr = refract(incident, n, eta);
            if (all(refr == 0.0))
            {
                refr = incident;
            }

            float baseHeight = thickness * gBackgroundDistance;
            float travel = (glassHeight + baseHeight) / max(0.001, abs(refr.z));
            float maxDisp = thickness * 1.6;
            float2 disp = clamp(refr.xy * travel, -maxDisp, maxDisp) * gLensing;

            float2 baseUV = gBarOriginUV + px / gDesktopSize;
            float2 dispUV = disp / gDesktopSize;

            // ---- Scatter, driven by how hard the lens is bending here ----
            // Where the mapping is the identity (the flat interior) the backdrop
            // must come through sharp -- that is what a plate of glass does.
            // Where the lens compresses a wide strip of backdrop into a few pixels
            // (the rim) that strip has to be low-passed or it aliases; that blur is
            // also what reads as the thick, dense edge.
            // A single uniform blur across the whole element is the cheap version:
            // it destroys every spatial frequency finer than the kernel, so nothing
            // behind the bar is recognisable and it reads as frosted acrylic.
            float dispMag = saturate(length(disp) / max(maxDisp, 1e-3));
            float scatter = lerp(saturate(1.0 - gClarity), 1.0, dispMag);

            // Dispersion: red bends more than blue. Physically inverted, reads better.
            float c = gChromatic;
            float2 uvR = baseUV + dispUV * (1.0 + c);
            float2 uvG = baseUV + dispUV;
            float2 uvB = baseUV + dispUV * (1.0 - c);

            float3 clear = float3(gSharp.Sample(gSampler, uvR).r,
                                  gSharp.Sample(gSampler, uvG).g,
                                  gSharp.Sample(gSampler, uvB).b);
            float3 diffuse = float3(gDesktop.Sample(gSampler, uvR).r,
                                    gDesktop.Sample(gSampler, uvG).g,
                                    gDesktop.Sample(gSampler, uvB).b);
            float3 mixed = lerp(clear, diffuse, scatter);
            float r0 = mixed.r;
            float g0 = mixed.g;
            float b0 = mixed.b;

            // Two opposing lights, Lorentzian rim band.
            float rimWidth = 1.5 * max(gScaling, 1.0);
            float x = sd / rimWidth;
            float rimFactor = 1.0 / (1.0 + 0.89 * x * x);

            float mainL = max(0.0, dot(n.xy, gLightDir));
            float oppL  = max(0.0, dot(n.xy, -gLightDir));
            float total = mainL + oppL * 0.8;

            float thicknessFactor = saturate((thickness - 5.0) * 0.5);
            float shape = saturate(nCos * 1.111);

            // The rim colour comes from the backdrop, not from fixed white.
            // Glass concentrates the light behind it; it does not emit its own.
            // A flat white rim is the signature of the cheap version.
            // White stays for dark or desaturated backdrops, backdrop-tinted for bright
            // saturated ones -- and the saturation boost is confined to the rim.
            float3 bg = float3(r0, g0, b0);
            float3 LUMA = float3(0.299, 0.587, 0.114);
            float lum = dot(bg, LUMA);
            float maxC = max(bg.r, max(bg.g, bg.b));
            float minC = min(bg.r, min(bg.g, bg.b));
            float sat = (maxC - minC) / max(maxC, 1e-4);
            float target = gIsDark > 0.5 ? 1.0 : 0.85;
            float3 colored = (bg / max(lum, 1e-4)) * target;
            colored = lerp(float3(dot(colored, LUMA), dot(colored, LUMA), dot(colored, LUMA)),
                           colored, 1.3);
            float infl = smoothstep(0.0, 0.6, lum) * smoothstep(0.0, 0.4, sat);
            float3 hiColor = lerp(float3(target, target, target), colored, infl);

            float3 rim = hiColor
                       * (0.7 * total * total * gLightIntensity * 2.0 + 0.4 * gAmbient)
                       * rimFactor * thicknessFactor * shape;

            // Dark band just inside the bright rim: adjacency is what reads as thickness.
            float innerShade = smoothstep(0.0, 0.6, nCos) * (1.0 - nCos) * 0.16;

            // Dynamic range compression. A high-contrast backdrop (text, busy photo)
            // is squeezed toward its own mean so the bar's own text stays legible.
            // p95-p05 is approximated as 3.29*rms; for a roughly normal distribution
            // that is the exact relation, and the clamp makes the error harmless.
            float spread = max(3.29 * bgRms, 1e-3);
            float k = clamp(0.34 / spread, 0.55, 1.0);
            bg = bgMean + (bg - bgMean) * k;

            // Tint as a luminance-indexed tone map, not a flat overlay:
            // one colour expands into a ramp of tones driven by backdrop brightness.
            float3 ramp = gTint * (0.35 + 0.65 * lum);
            float amt = gTintAmount * (0.25 + 0.35 * nCos);
            float3 tinted = lerp(bg, ramp, amt);
            float tlum = dot(tinted, LUMA);
            tinted = lerp(float3(tlum, tlum, tlum), tinted, 1.12);

            float3 col = saturate(tinted + rim - innerShade);

            // Glass over shadow, straight alpha. The shadow is pure black, so its
            // colour contribution is zero either way.
            float outA = coverage + shadowA * (1.0 - coverage);
            return float4(col * coverage / max(outA, 1e-4), outA);
        }
        """;
}
