@tool
extends RefCounted

const Catalog = preload("res://addons/dirge/runtime/asset_catalog.gd")
var assets
var banks: Dictionary = {}
var _inflight: Dictionary = {}

func _init(catalog = null) -> void:
	assets = catalog if catalog != null else Catalog.new()

func is_loaded(bank_name: String) -> bool:
	return banks.has(bank_name) and banks[bank_name].pending == 0

func pending_count(bank_name: String) -> int:
	return banks[bank_name].pending if banks.has(bank_name) else -1

func load_bank(project: Dictionary, bank_name: String) -> bool:
	if banks.has(bank_name):
		return true
	var authored: Dictionary = {}
	for bank in project.get("banks", []):
		if bank.get("name", "") == bank_name:
			authored = bank
			break
	if authored.is_empty():
		push_error("Dirge: unknown bank '%s'." % bank_name)
		return false
	var paths: Array = []
	var visited: Dictionary = {}
	for event_id in authored.get("events", []):
		_collect_event(project, event_id, paths, visited)
	var bank = {"paths": paths, "pending": 0}
	banks[bank_name] = bank
	for path in paths:
		if assets.is_cached(path):
			continue
		if _inflight.has(path):
			bank.pending += 1
			continue
		var error = ResourceLoader.load_threaded_request(path, "AudioStream", true)
		if error != OK:
			push_error("Dirge: bank '%s' cannot request '%s': %s." % [bank_name, path, error])
			assets.register(path, null)
			continue
		_inflight[path] = true
		bank.pending += 1
	return true

func unload(bank_name: String) -> void:
	if not banks.has(bank_name):
		return
	var bank: Dictionary = banks[bank_name]
	banks.erase(bank_name)
	for path in bank.paths:
		if not _inflight.has(path) and not _is_referenced(path):
			assets.evict(path)

func poll() -> void:
	for path in _inflight.keys():
		var status = ResourceLoader.load_threaded_get_status(path)
		if status == ResourceLoader.THREAD_LOAD_IN_PROGRESS:
			continue
		_inflight.erase(path)
		var stream = ResourceLoader.load_threaded_get(path) as AudioStream if status == ResourceLoader.THREAD_LOAD_LOADED else null
		if stream == null:
			push_error("Dirge: bank asset '%s' failed to load." % path)
		if _is_referenced(path):
			assets.register(path, stream)
		for bank in banks.values():
			if bank.pending > 0 and path in bank.paths:
				bank.pending -= 1

func _is_referenced(path: String) -> bool:
	for bank in banks.values():
		if path in bank.paths:
			return true
	return false

func _collect_event(project: Dictionary, event_id: String, paths: Array, visited: Dictionary) -> void:
	if visited.has(event_id):
		return
	visited[event_id] = true
	for event in project.get("events", []):
		if event.get("id", "") != event_id:
			continue
		for track in event.get("tracks", []):
			for instrument in track.get("instruments", []):
				match instrument.get("$type", ""):
					"Clip":
						_add_path(paths, instrument.get("asset", ""))
					"Multi", "Scatterer":
						for entry in instrument.get("entries", []):
							_add_path(paths, entry.get("asset", ""))
					"Event":
						_collect_event(project, instrument.get("event", ""), paths, visited)
		return

func _add_path(paths: Array, asset) -> void:
	var path = Catalog.asset_path(asset)
	if not path.is_empty() and not path in paths:
		paths.append(path)
