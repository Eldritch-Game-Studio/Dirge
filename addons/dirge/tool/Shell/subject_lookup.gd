@tool
extends RefCounted
const Selection = preload("res://addons/dirge/tool/Core/selection.gd")

static func resolve(project: Dictionary, id: String) -> Dictionary:
	if id.is_empty() or id == "00000000000000000000000000000000": return {}
	for bus in project.get("mixer", {}).get("buses", []):
		if bus.id == id: return _subject(Selection.Kind.BUS, id, "", -1, bus.name)
		for index in bus.get("effects", []).size():
			var effect = bus.effects[index]
			if effect.id == id: return _subject(Selection.Kind.EFFECT, id, bus.id, index, bus.name + "/" + effect.typeName)
	for vca in project.get("mixer", {}).get("vcas", []):
		if vca.id == id: return _subject(Selection.Kind.VCA, id, "", -1, vca.name)
	for pair in [["snapshots", Selection.Kind.SNAPSHOT], ["globalParameters", Selection.Kind.PARAMETER], ["banks", Selection.Kind.BANK]]:
		for item in project.get(pair[0], []):
			if item.id == id: return _subject(pair[1], id, "", -1, item.name)
	for event in project.get("events", []):
		if event.id == id: return _subject(Selection.Kind.EVENT, id, "", -1, event.path)
		for pair in [["localParameters", Selection.Kind.PARAMETER], ["tracks", Selection.Kind.TRACK], ["markers", Selection.Kind.MARKER]]:
			for item in event.get(pair[0], []):
				if item.id == id: return _subject(pair[1], id, event.id, -1, event.path + "/" + str(item.get("name", item.get("$type", ""))))
		for track in event.get("tracks", []):
			for item in track.get("instruments", []):
				if item.id == id: return _subject(Selection.Kind.INSTRUMENT, id, event.id, -1, event.path + "/" + str(item.get("name", item.get("$type", ""))))
	return {}

static func _subject(kind: int, id: String, owner: String, index: int, name: String) -> Dictionary:
	return {"kind": kind, "id": id, "owner": owner, "index": index, "name": name}
