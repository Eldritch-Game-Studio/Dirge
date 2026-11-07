using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;
using Godot;

namespace Dirge.Driver;

/// <summary>
/// Loads <see cref="AudioStream"/>s through <see cref="ResourceLoader"/> once and answers the model's length queries from
/// <see cref="AudioStream.GetLength"/>. Looping requests get a per-asset duplicate so the imported resource keeps its authored loop setting.
/// </summary>
public sealed class AssetCatalog : IAssetCatalog
{
    private readonly Dictionary<string, AudioStream?> _streams = new();
    private readonly Dictionary<string, AudioStream?> _looping = new();
    private readonly Dictionary<string, double> _lengths = new();

    public bool TryGetLength(AssetRef asset, out double seconds)
    {
        if (_lengths.TryGetValue(asset.Path, out seconds))
        {
            return seconds > 0;
        }

        AudioStream? stream = GetStream(asset);
        seconds = stream is null ? 0 : stream.GetLength();
        _lengths[asset.Path] = seconds;
        return seconds > 0;
    }

    /// <summary>The asset as imported; <see langword="null"/> when it cannot be loaded (logged once).</summary>
    public AudioStream? GetStream(AssetRef asset)
    {
        if (_streams.TryGetValue(asset.Path, out AudioStream? cached))
        {
            return cached;
        }

        AudioStream? stream = asset.IsEmpty ? null : ResourceLoader.Load<AudioStream>(asset.Path);
        if (stream is null)
        {
            GD.PushError($"Dirge: cannot load audio asset '{asset.Path}'.");
        }

        _streams[asset.Path] = stream;
        return stream;
    }

    /// <summary>True when <paramref name="path"/> has a cache entry (loaded or known-missing).</summary>
    public bool IsCached(string path) => _streams.ContainsKey(path);

    /// <summary>Seeds the cache with a stream loaded elsewhere (bank preloads); replaces any existing entry.</summary>
    public void Register(string path, AudioStream? stream)
    {
        _streams[path] = stream;
        _looping.Remove(path);
        _lengths.Remove(path);
        if (stream is not null)
        {
            _lengths[path] = stream.GetLength();
        }
    }

    /// <summary>Drops every cache entry for <paramref name="path"/>; players already holding the stream keep it alive.</summary>
    public void Evict(string path)
    {
        _streams.Remove(path);
        _looping.Remove(path);
        _lengths.Remove(path);
    }

    /// <summary>A copy of the asset configured to loop whole; shares sample data with the imported resource.</summary>
    public AudioStream? GetLoopingStream(AssetRef asset)
    {
        if (_looping.TryGetValue(asset.Path, out AudioStream? cached))
        {
            return cached;
        }

        AudioStream? looping = MakeLooping(GetStream(asset));
        _looping[asset.Path] = looping;
        return looping;
    }

    private static AudioStream? MakeLooping(AudioStream? source)
    {
        switch (source)
        {
            case null:
                return null;
            case AudioStreamWav wav:
            {
                if (wav.LoopMode != AudioStreamWav.LoopModeEnum.Disabled)
                {
                    return wav;
                }

                var copy = (AudioStreamWav)wav.Duplicate();
                copy.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                copy.LoopBegin = 0;
                copy.LoopEnd = (int)System.Math.Round(wav.GetLength() * wav.MixRate);
                return copy;
            }
            case AudioStreamOggVorbis ogg:
            {
                if (ogg.Loop)
                {
                    return ogg;
                }

                var copy = (AudioStreamOggVorbis)ogg.Duplicate();
                copy.Loop = true;
                return copy;
            }
            case AudioStreamMP3 mp3:
            {
                if (mp3.Loop)
                {
                    return mp3;
                }

                var copy = (AudioStreamMP3)mp3.Duplicate();
                copy.Loop = true;
                return copy;
            }
            default:
                // Other stream types (playlists, interactive, synchronized) carry their own loop configuration.
                return source;
        }
    }
}
