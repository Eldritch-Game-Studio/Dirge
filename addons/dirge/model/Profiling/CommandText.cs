using System.Globalization;
using System.Numerics;
using System.Text;
using Dirge.Runtime;

namespace Dirge.Profiling;

/// <summary>
/// Culture-invariant, round-trip-exact text for <see cref="Command"/> records so recordings compare identically across
/// machines (record <c>ToString()</c> would format floats with the current culture). Format: <c>Type(arg, arg, …)</c>.
/// </summary>
public static class CommandText
{
    [System.ThreadStatic] private static StringBuilder? _builder;

    public static string Format(Command command)
    {
        StringBuilder sb = _builder ??= new StringBuilder(128);
        sb.Clear();
        switch (command)
        {
            case VoicePlay p:
                sb.Append("VoicePlay(").Append(p.Voice.Value).Append(", ").Append(p.Asset.Path).Append(", ").Append(p.Bus).Append(", ");
                Num(sb, p.StartSeconds).Append(", ").Append(p.Loop ? "loop" : "once").Append(", ");
                Num(sb, p.VolumeDb).Append(" dB, ");
                Num(sb, p.Pitch).Append("x, ");
                if (p.Spatial) { sb.Append("3d "); Attr(sb, p.Attributes).Append(", "); Num(sb, p.MinDistance).Append("..").Num(p.MaxDistance).Append(", "); }
                else sb.Append("2d, ");
                sb.Append("sync ").Append(p.SyncGroup).Append(')');
                break;
            case VoiceStop s:
                sb.Append("VoiceStop(").Append(s.Voice.Value).Append(')');
                break;
            case VoiceSeek s:
                sb.Append("VoiceSeek(").Append(s.Voice.Value).Append(", "); Num(sb, s.Seconds).Append(')');
                break;
            case VoiceSetVolume v:
                sb.Append("VoiceSetVolume(").Append(v.Voice.Value).Append(", "); Num(sb, v.VolumeDb).Append(')');
                break;
            case VoiceSetPitch v:
                sb.Append("VoiceSetPitch(").Append(v.Voice.Value).Append(", "); Num(sb, v.Pitch).Append(')');
                break;
            case VoiceSet3D v:
                sb.Append("VoiceSet3D(").Append(v.Voice.Value).Append(", "); Attr(sb, v.Attributes).Append(')');
                break;
            case VoiceSetPaused v:
                sb.Append("VoiceSetPaused(").Append(v.Voice.Value).Append(", ").Append(v.Paused).Append(')');
                break;
            case BusSetVolume b:
                sb.Append("BusSetVolume(").Append(b.Bus).Append(", "); Num(sb, b.VolumeDb).Append(')');
                break;
            case BusSetMute b:
                sb.Append("BusSetMute(").Append(b.Bus).Append(", ").Append(b.Mute).Append(')');
                break;
            case BusSetSolo b:
                sb.Append("BusSetSolo(").Append(b.Bus).Append(", ").Append(b.Solo).Append(')');
                break;
            case BusSetBypass b:
                sb.Append("BusSetBypass(").Append(b.Bus).Append(", ").Append(b.Bypass).Append(')');
                break;
            case EffectSetEnabled e:
                sb.Append("EffectSetEnabled(").Append(e.Bus).Append(", ").Append(e.EffectIndex).Append(", ").Append(e.Enabled).Append(')');
                break;
            case EffectSetParameter e:
                sb.Append("EffectSetParameter(").Append(e.Bus).Append(", ").Append(e.EffectIndex).Append(", ").Append(e.Parameter).Append(", "); Num(sb, e.Value).Append(')');
                break;
            default:
                sb.Append(command.ToString());
                break;
        }
        return sb.ToString();
    }

    private static StringBuilder Num(this StringBuilder sb, double value) => sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
    private static StringBuilder Num(this StringBuilder sb, float value) => sb.Append(value.ToString("R", CultureInfo.InvariantCulture));

    private static StringBuilder Vec(StringBuilder sb, Vector3 v)
    {
        sb.Append('['); Num(sb, v.X).Append(' '); Num(sb, v.Y).Append(' '); Num(sb, v.Z).Append(']');
        return sb;
    }

    private static StringBuilder Attr(StringBuilder sb, in Attributes3D a)
    {
        sb.Append("p="); Vec(sb, a.Position).Append(" v="); Vec(sb, a.Velocity).Append(" f="); Vec(sb, a.Forward).Append(" u="); Vec(sb, a.Up);
        if (a.Region != default) sb.Append(" region=").Append(a.Region.Value.ToString(CultureInfo.InvariantCulture));
        return sb;
    }
}
