@tool
extends RefCounted

var _streams: Dictionary = {}
var _looping: Dictionary = {}
var _lengths: Dictionary = {}

static func asset_path(asset) -> String:
	return str(asset.get("path", "")) if asset is Dictionary else str(asset)

func get_length(asset) -> float:
	var path = asset_path(asset)
	if not _lengths.has(path):
		var stream = get_stream(path)
		_lengths[path] = stream.get_length() if stream != null else 0.0
	return _lengths[path]

func try_get_length(asset) -> float:
	return get_length(asset)

func get_stream(asset) -> AudioStream:
	var path = asset_path(asset)
	if not _streams.has(path):
		var stream = ResourceLoader.load(path, "AudioStream") as AudioStream if not path.is_empty() and ResourceLoader.exists(path) else null
		if stream == null:
			push_error("Dirge: cannot load audio asset '%s'." % path)
		_streams[path] = stream
	return _streams[path]

func is_cached(path: String) -> bool:
	return _streams.has(path)

func register(path: String, stream: AudioStream) -> void:
	_streams[path] = stream
	_looping.erase(path)
	_lengths.erase(path)
	if stream != null:
		_lengths[path] = stream.get_length()

func evict(path: String) -> void:
	_streams.erase(path)
	_looping.erase(path)
	_lengths.erase(path)

func get_looping_stream(asset) -> AudioStream:
	var path = asset_path(asset)
	if _looping.has(path):
		return _looping[path]
	var source = get_stream(path)
	var stream = source
	if source is AudioStreamWAV and source.loop_mode == AudioStreamWAV.LOOP_DISABLED:
		stream = source.duplicate()
		stream.loop_mode = AudioStreamWAV.LOOP_FORWARD
		stream.loop_begin = 0
		stream.loop_end = roundi(source.get_length() * source.mix_rate)
	elif (source is AudioStreamOggVorbis or source is AudioStreamMP3) and not source.loop:
		stream = source.duplicate()
		stream.loop = true
	_looping[path] = stream
	return stream
