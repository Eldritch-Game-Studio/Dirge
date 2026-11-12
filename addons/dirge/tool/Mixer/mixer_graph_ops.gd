@tool
extends RefCounted
## Dictionary edits only. The caller owns the ProjectDocument transaction.
const NONE = "00000000000000000000000000000000"
const AFTER_ALL = 2147483647

static func new_id() -> String:
	return Crypto.new().generate_random_bytes(16).hex_encode()

static func find(items: Array, id: String) -> Dictionary:
	for item in items:
		if item.get("id", "") == id:
			return item
	return {}

static func master(mixer: Dictionary) -> Dictionary:
	for bus in mixer.get("buses", []):
		if bus.get("parent", NONE) in [NONE, "", null]:
			return bus
	return {}

static func find_bus(mixer: Dictionary, id: String) -> Dictionary:
	return find(mixer.get("buses", []), id)

static func is_self_or_descendant(mixer: Dictionary, candidate: String, ancestor: String) -> bool:
	var remaining = mixer.get("buses", []).size() + 1
	while candidate != NONE and candidate != "" and remaining > 0:
		if candidate == ancestor:
			return true
		var bus = find_bus(mixer, candidate)
		if bus.is_empty():
			return false
		candidate = str(bus.get("parent", NONE))
		remaining -= 1
	return false

static func routing_targets(mixer: Dictionary, id: String) -> Array:
	return mixer.get("buses", []).filter(func(bus): return not is_self_or_descendant(mixer, bus.id, id))

static func unique_name(items: Array, base: String) -> String:
	var names = {}
	for item in items:
		names[item.get("name", "")] = true
	var name = base
	var suffix = 2
	while names.has(name):
		name = base + " " + str(suffix)
		suffix += 1
	return name

static func property(kind: String, owner: String, index: int = -1, name = null) -> Dictionary:
	return {"kind": kind, "owner": owner, "effectIndex": index, "name": name}

static func target_equal(a: Dictionary, b: Dictionary) -> bool:
	return a.get("kind") == b.get("kind") and a.get("owner") == b.get("owner") and int(a.get("effectIndex", -1)) == int(b.get("effectIndex", -1)) and a.get("name") == b.get("name")

static func find_value(snapshot: Dictionary, target: Dictionary) -> Dictionary:
	for value in snapshot.get("values", []):
		if target_equal(value.get("target", {}), target):
			return value
	return {}

static func set_value(snapshot: Dictionary, target: Dictionary, value: float) -> void:
	var existing = find_value(snapshot, target)
	if existing.is_empty():
		snapshot.get_or_add("values", []).append({"target": target.duplicate(true), "value": value})
	else:
		existing.value = value

static func unscope(snapshot: Dictionary, target: Dictionary) -> void:
	var values = snapshot.get("values", [])
	for index in range(values.size() - 1, -1, -1):
		if target_equal(values[index].get("target", {}), target):
			values.remove_at(index)

static func delete_bus(project: Dictionary, id: String) -> bool:
	var mixer = project.get("mixer", {})
	var bus = find_bus(mixer, id)
	if bus.is_empty() or bus == master(mixer):
		return false
	for child in mixer.get("buses", []):
		if child.get("parent") == id:
			child.parent = bus.parent
	for event in project.get("events", []):
		if event.get("bus") == id:
			event.bus = bus.parent
	_remove_owner_targets(project, id)
	mixer.buses.erase(bus)
	return true

static func _target_lists(project: Dictionary) -> Array:
	var lists: Array = []
	for snapshot in project.get("snapshots", []):
		lists.append(snapshot.get("values", []))
	lists.append(project.get("mixerAutomation", []))
	lists.append(project.get("mixerModulators", []))
	return lists

static func _remove_owner_targets(project: Dictionary, id: String) -> void:
	for items in _target_lists(project):
		for index in range(items.size() - 1, -1, -1):
			if items[index].get("target", {}).get("owner") == id:
				items.remove_at(index)

static func delete_vca(project: Dictionary, id: String) -> void:
	var mixer = project.get("mixer", {})
	var vca = find(mixer.get("vcas", []), id)
	if vca.is_empty():
		return
	mixer.vcas.erase(vca)
	for bus in mixer.get("buses", []):
		bus.get("vcas", []).erase(id)
	_remove_owner_targets(project, id)

static func move_bus(mixer: Dictionary, id: String, to: int) -> void:
	var buses = mixer.get("buses", [])
	var bus = find_bus(mixer, id)
	if bus.is_empty() or bus == master(mixer) or to < 0 or to >= buses.size():
		return
	if buses[to] == master(mixer):
		return
	buses.erase(bus)
	buses.insert(to, bus)

static func _remap_effect_targets(project: Dictionary, id: String, mapping: Callable) -> void:
	for items in _target_lists(project):
		for i in range(items.size() - 1, -1, -1):
			var target = items[i].get("target", {})
			if target.get("kind") != "EffectParameter" or target.get("owner") != id:
				continue
			var index = mapping.call(int(target.get("effectIndex", -1)))
			if index < 0:
				items.remove_at(i)
			else:
				target.effectIndex = index

static func insert_effect(project: Dictionary, id: String, effect: Dictionary, index: int) -> void:
	var bus = find_bus(project.get("mixer", {}), id)
	if bus.is_empty():
		return
	var effects = bus.get_or_add("effects", [])
	index = clampi(index, 0, effects.size())
	_remap_effect_targets(project, id, func(i): return i + 1 if i >= index else i)
	effects.insert(index, effect)
	var fader = int(bus.get("faderIndex", AFTER_ALL))
	if fader != AFTER_ALL and fader >= index:
		bus.faderIndex = fader + 1

static func remove_effect(project: Dictionary, id: String, index: int) -> void:
	var bus = find_bus(project.get("mixer", {}), id)
	var effects = bus.get("effects", [])
	if index < 0 or index >= effects.size():
		return
	effects.remove_at(index)
	_remap_effect_targets(project, id, func(i): return -1 if i == index else i - 1 if i > index else i)
	var fader = int(bus.get("faderIndex", AFTER_ALL))
	if fader != AFTER_ALL and fader > index:
		bus.faderIndex = fader - 1

static func move_effect(project: Dictionary, id: String, from: int, to: int) -> void:
	var effects = find_bus(project.get("mixer", {}), id).get("effects", [])
	if from == to or mini(from, to) < 0 or maxi(from, to) >= effects.size():
		return
	var effect = effects[from]
	effects.remove_at(from)
	effects.insert(to, effect)
	_remap_effect_targets(project, id, func(i):
		if i == from: return to
		if from < to and i > from and i <= to: return i - 1
		if to < from and i >= to and i < from: return i + 1
		return i)

static func move_chain_row(project: Dictionary, id: String, from_slot: int, to_slot: int) -> void:
	var bus = find_bus(project.get("mixer", {}), id)
	if bus.is_empty():
		return
	var count = bus.get("effects", []).size()
	if mini(from_slot, to_slot) < 0 or maxi(from_slot, to_slot) > count or from_slot == to_slot:
		return
	var fader = clampi(int(bus.get("faderIndex", AFTER_ALL)), 0, count)
	var display: Array = []
	for slot in range(count + 1):
		display.append(-1 if slot == fader else slot if slot < fader else slot - 1)
	var moved = display[from_slot]
	display.remove_at(from_slot)
	display.insert(to_slot, moved)
	var next_fader = display.find(-1)
	if moved >= 0:
		var target = to_slot - (1 if next_fader < to_slot else 0)
		move_effect(project, id, moved, target)
	bus.faderIndex = AFTER_ALL if next_fader == count else next_fader
