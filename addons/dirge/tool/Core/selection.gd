@tool
extends RefCounted

enum Kind { NONE, EVENT, INSTRUMENT, TRACK, MARKER, BUS, EFFECT, VCA, SNAPSHOT, PARAMETER, ASSET, BANK }
signal changed
var kind: int = Kind.NONE
var id: String = ""
var owner: String = ""
var index: int = -1
var asset_path: String = ""

func select(value: int, selected_id: String, owner_id: String = "", selected_index: int = -1) -> void:
	kind = value
	id = selected_id
	owner = owner_id
	index = selected_index
	asset_path = ""
	changed.emit()

func select_asset(path: String) -> void:
	kind = Kind.ASSET
	id = ""
	owner = ""
	index = -1
	asset_path = path
	changed.emit()

func clear() -> void:
	select(Kind.NONE, "")
