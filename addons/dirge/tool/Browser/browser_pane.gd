@tool
extends Control
const Docs = preload("res://addons/dirge/tool/Core/tool_documents.gd")
const Model = preload("res://addons/dirge/model/Project/project_model.gd")
const TreeModel = preload("res://addons/dirge/tool/Browser/event_tree.gd")
const Cloner = preload("res://addons/dirge/tool/Browser/event_cloner.gd")
const Scanner = preload("res://addons/dirge/tool/Browser/asset_scanner.gd")
const Selection = preload("res://addons/dirge/tool/Core/selection.gd")
const Commands = preload("res://addons/dirge/tool/Core/tool_commands.gd")
signal event_selected(id: String)
signal subject_selected(kind: int, id: String)
var document: RefCounted
var server: Node
var selection: RefCounted
var host: Control
var active_tab: String = "events"
var query: String = ""
var tag_filter: String = ""
var selected_folder: String = ""
var selected_bank: String = ""
var empty_folders: Array[String] = []
var collapsed: Dictionary = {}
var expanded_assets: Dictionary = {}
var scanner = Scanner.new()
var asset_root: Dictionary = {}
var _rows: Dictionary = {}
var _row_order: Array[String] = []
var _selected_row: String = ""
var _rename: Dictionary = {}
var _preview: AudioStreamPlayer
var _menu: PopupMenu
var _confirm: ConfirmationDialog
var _pending: Callable
var _menu_row: Dictionary = {}
var _last_click: String = ""
var _last_click_ms: int = 0
var _clipboard: Dictionary = {}
var _tag_signature: String = ""
var _chip_signature: String = ""

func setup(value: RefCounted, audition: Node = null, shared_selection: RefCounted = null) -> void:
	document = value
	server = audition
	selection = shared_selection if shared_selection != null else Selection.new()
	host = Docs.create("browser.html")
	host.mouse_filter = Control.MOUSE_FILTER_PASS
	var frame = Docs.slot(host)
	add_child(frame)
	frame.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	host.connect("clicked", _clicked)
	host.connect("dom_event_detail", _dom_event)
	for type in ["input", "change", "keydown", "focusout", "contextmenu"]:
		host.call("listen", type)
	host.set_drag_forwarding(_get_drag, _can_drop, _drop)
	_preview = AudioStreamPlayer.new()
	add_child(_preview)
	_menu = PopupMenu.new()
	_menu.id_pressed.connect(_menu_action)
	add_child(_menu)
	_confirm = ConfirmationDialog.new()
	_confirm.title = "Dirge"
	_confirm.confirmed.connect(func():
		if _pending.is_valid():
			_pending.call()
		_pending = Callable())
	_confirm.canceled.connect(func(): _pending = Callable())
	add_child(_confirm)
	document.changed.connect(_document_changed)
	selection.changed.connect(_selection_changed)
	asset_root = scanner.scan()
	show_tab("events")

func _exit_tree() -> void:
	if document != null and document.changed.is_connected(_document_changed):
		document.changed.disconnect(_document_changed)
	if selection != null and selection.changed.is_connected(_selection_changed):
		selection.changed.disconnect(_selection_changed)

func show_tab(tab: String) -> void:
	cancel_rename()
	active_tab = tab
	for name in ["events", "assets", "snapshots", "parameters", "banks"]:
		Docs.class_state(host, "#tab-" + name, "active", name == tab)
	Docs.hidden(host, "#members", tab != "banks")
	Docs.hidden(host, "#import-lengths", tab != "assets")
	Docs.hidden(host, "#add-folder", tab != "events")
	Docs.hidden(host, "#add", tab == "assets")
	Docs.hidden(host, "#tag-filter", tab != "events")
	host.call("set_text", "#add", {"events": "+ Event", "snapshots": "+ Snapshot", "parameters": "+ Parameter", "banks": "+ Bank"}.get(tab, "+"))
	rebuild()

func _document_changed() -> void:
	if not document.dirty and document.changed_scope == document.ChangeScope.ALL:
		empty_folders.clear()
		asset_root = scanner.scan()
	var relevant = {"events": document.ChangeScope.EVENTS, "assets": document.ChangeScope.ASSETS, "snapshots": document.ChangeScope.SNAPSHOTS, "parameters": document.ChangeScope.GLOBAL_PARAMETERS, "banks": document.ChangeScope.EVENTS}.get(active_tab, 0)
	if document.changed_scope & relevant:
		rebuild()

func rebuild() -> void:
	if host == null:
		return
	var rows: Array[Dictionary] = []
	match active_tab:
		"events":
			_event_rows(TreeModel.build(document.project.events, empty_folders, query, tag_filter), 0, rows)
			_tags()
		"assets": _asset_rows(Scanner.filter(asset_root, query), 0, rows)
		_:
			var key = _collection()
			for item in document.project.get(key, []):
				if not query.is_empty() and not str(item.name).to_lower().contains(query.to_lower()):
					continue
				var meta = ""
				if active_tab == "snapshots": meta = "%s · %s" % [item.mode, item.priority]
				if active_tab == "parameters": meta = "%s · %s … %s" % [item.kind, item.minimum, item.maximum]
				if active_tab == "banks": meta = "%s events" % item.events.size()
				rows.append(_row(active_tab.trim_suffix("s") if active_tab != "parameters" else "parameter", item.id, item.name, meta))
	if rows.is_empty():
		rows.append(_row("empty", "empty", "No %s%s" % [active_tab, " match" if not query.is_empty() else ""], ""))
	_sync_rows(rows)
	_members()
	_selection_changed()
	var count = scanner.files.size() if active_tab == "assets" else document.project.get(_collection(), []).size()
	host.call("set_text", "#status", "%d %s" % [count, active_tab])

func _collection() -> String:
	return "globalParameters" if active_tab == "parameters" else active_tab

func _row(kind: String, key: String, label: String, meta: String, depth: int = 0, twisty: String = "") -> Dictionary:
	return {"kind": kind, "key": key, "label": label, "meta": meta, "depth": mini(depth, 8), "twisty": twisty, "dom": "row-" + (kind + ":" + key).sha256_text().substr(0, 24)}

func _event_rows(node: Dictionary, depth: int, rows: Array[Dictionary]) -> void:
	for child in node.children:
		if child.is_folder:
			var shut = query.is_empty() and tag_filter.is_empty() and collapsed.has(child.full_path)
			rows.append(_row("folder", child.full_path, child.name, str(_leaf_count(child)), depth, "+" if shut else "−"))
			if not shut:
				_event_rows(child, depth + 1, rows)
		else:
			var event = find_event(child.event_id)
			rows.append(_row("event", child.event_id, child.name, "3D" if event.get("macros", {}).get("is3D", false) else "", depth))

func _asset_rows(node: Dictionary, depth: int, rows: Array[Dictionary]) -> void:
	for child in node.children:
		if child.is_folder:
			var open = not query.is_empty() or expanded_assets.has(child.path)
			rows.append(_row("afolder", child.path, child.name, str(_leaf_count(child)), depth, "−" if open else "+"))
			if open:
				_asset_rows(child, depth + 1, rows)
		else:
			var seconds = document.project.get("assetLengths", {}).get(child.path, -1.0)
			rows.append(_row("asset", child.path, child.name, "%.2f s" % seconds if seconds >= 0.0 else "", depth))

func _leaf_count(node: Dictionary) -> int:
	var count = 0
	for child in node.children:
		count += _leaf_count(child) if child.is_folder else 1
	return count

func _sync_rows(rows: Array[Dictionary]) -> void:
	var desired: Dictionary = {}
	var order: Array[String] = []
	for row in rows:
		var id: String = row.dom
		desired[id] = row
		order.append(id)
		if not _rows.has(id):
			host.call("append_html", "#tree", '<div id="%s" class="node %s d%d"><p id="%s-twisty" class="twisty"></p><div class="icon"></div><p class="name"></p><p class="meta"></p></div>' % [id, row.kind, row.depth, id])
		if _rows.get(id, {}) != row:
			host.call("set_attribute", "#" + id, "class", "node %s d%d" % [row.kind, row.depth])
			host.call("set_text", "#" + id + " .name", row.label)
			host.call("set_text", "#" + id + " .meta", row.meta)
			host.call("set_text", "#" + id + " .twisty", row.twisty)
	for id in _rows:
		if not desired.has(id):
			host.call("remove_element", "#" + id)
	if order != _row_order:
		for id in order:
			host.call("move_element", "#" + id, "#tree", "")
	_rows = desired
	_row_order = order

func _tags() -> void:
	var tags = TreeModel.all_tags(document.project.events)
	if not tags.has(tag_filter):
		tag_filter = ""
	var signature = JSON.stringify([tags, tag_filter])
	if signature == _tag_signature:
		return
	_tag_signature = signature
	var html = ""
	if not tags.is_empty():
		html = '<button id="tag-all" class="chip clear%s">All</button>' % (" on" if tag_filter.is_empty() else "")
	for index in tags.size():
		html += '<button id="tag-%d" class="chip%s" data-tag="%s">%s</button>' % [index, " on" if tags[index] == tag_filter else "", Docs.escape(tags[index]), Docs.escape(tags[index])]
	host.call("set_inner_html", "#tag-filter", html)

func _selection_changed() -> void:
	_selected_row = ""
	for id in _rows:
		var row = _rows[id]
		var selected = (row.kind == "event" and selection.kind == Selection.Kind.EVENT and row.key == selection.id) or (row.kind == "folder" and selection.kind == Selection.Kind.NONE and row.key == selected_folder) or (row.kind == "asset" and row.key == selection.asset_path) or (row.kind == "bank" and row.key == selected_bank) or (row.kind in ["snapshot", "parameter"] and row.key == selection.id)
		Docs.class_state(host, "#" + id, "selected", selected)
		if selected: _selected_row = id
	var event = find_event(selection.id) if selection.kind == Selection.Kind.EVENT else {}
	Docs.hidden(host, "#tag-editor", active_tab != "events" or event.is_empty())
	var tags: Array = event.get("tags", [])
	var signature = JSON.stringify(tags)
	if signature != _chip_signature:
		_chip_signature = signature
		var html = ""
		for index in tags.size():
			html += '<button id="remove-tag-%d" class="chip" data-tag="%s">%s ×</button>' % [index, Docs.escape(tags[index]), Docs.escape(tags[index])]
		host.call("set_inner_html", "#tag-chips", html)

func _members() -> void:
	if active_tab != "banks": return
	var bank = _find("banks", selected_bank)
	var html = ""
	host.call("set_text", ".members-heading", 'Events in "%s"' % bank.name if not bank.is_empty() else "Select a bank to edit its events")
	if not bank.is_empty():
		var events: Array = document.project.events.duplicate()
		events.sort_custom(func(a,b): return str(a.path).to_lower() < str(b.path).to_lower())
		for event in events:
			html += '<div id="member-%s" class="node member d0"><div class="check%s"></div><p class="name">%s</p></div>' % [event.id, " on" if bank.events.has(event.id) else "", Docs.escape(event.path)]
	var signature = html.sha256_text()
	if str(host.get_meta("members_signature", "")) != signature:
		host.set_meta("members_signature", signature)
		host.call("set_inner_html", "#member-list", html)

func _clicked(id: String) -> void:
	if id.begins_with("tab-"):
		show_tab(id.trim_prefix("tab-"))
		return
	match id:
		"add": add_item(); return
		"add-folder": new_folder(_folder()); return
		"import-lengths": import_lengths(); return
		"tag-all": tag_filter = ""; rebuild(); return
	if id.begins_with("tag-") and id != "tag-input":
		var tag = str(host.call("get_attribute", "#" + id, "data-tag"))
		tag_filter = "" if tag == tag_filter else tag
		rebuild()
		return
	if id.begins_with("remove-tag-"):
		remove_tag(selection.id, host.call("get_attribute", "#" + id, "data-tag"))
		return
	if id.begins_with("member-"):
		toggle_bank_membership(selected_bank, id.trim_prefix("member-"))
		return
	var twisty = id.ends_with("-twisty")
	id = id.trim_suffix("-twisty")
	if not _rows.has(id) or _rename.get("dom", "") == id:
		return
	commit_rename()
	var row = _rows[id]
	var second = id == _last_click and Time.get_ticks_msec() - _last_click_ms <= 400
	_last_click = id
	_last_click_ms = Time.get_ticks_msec()
	match row.kind:
		"folder":
			selected_folder = row.key
			selection.clear()
			if twisty or second:
				if collapsed.has(row.key): collapsed.erase(row.key)
				else: collapsed[row.key] = true
				rebuild()
		"afolder":
			if expanded_assets.has(row.key): expanded_assets.erase(row.key)
			else: expanded_assets[row.key] = true
			rebuild()
		"event":
			selection.select(Selection.Kind.EVENT, row.key)
			event_selected.emit(row.key)
			if second and server != null: server.StartEvent(find_event(row.key).path)
		"asset":
			selection.select_asset(row.key)
			if second: preview_asset(row.key)
		"snapshot", "parameter", "bank":
			var kind = {"snapshot": Selection.Kind.SNAPSHOT, "parameter": Selection.Kind.PARAMETER, "bank": Selection.Kind.BANK}[row.kind]
			if row.kind == "bank": selected_bank = row.key
			selection.select(kind, row.key)
			subject_selected.emit(kind, row.key)
			_members()
			if second: begin_rename(id)

func _dom_event(type: String, id: String, details: Dictionary) -> void:
	if type == "input" and id == "search":
		query = str(details.get("value", "")).strip_edges()
		rebuild()
	elif id == "tag-input" and (type == "change" or type == "keydown" and details.get("key") == "Enter"):
		var text = host.call("get_value", "#tag-input")
		host.call("set_value", "#tag-input", "")
		add_tag(selection.id, text)
	elif not _rename.is_empty() and id == "browser-rename":
		if type == "keydown" and details.get("key") == "Escape": cancel_rename()
		elif type == "focusout" or type == "keydown" and details.get("key") == "Enter": commit_rename()
	elif type == "contextmenu":
		_context(id.trim_suffix("-twisty"))

func begin_rename(id: String) -> void:
	commit_rename()
	if not _rows.has(id): return
	_rename = _rows[id].duplicate()
	host.call("set_attribute", "#" + id + " .name", "hidden", "")
	host.call("append_html", "#" + id, '<input id="browser-rename" class="rename" type="text" value="%s">' % Docs.escape(_rename.label))
	host.call("focus_element", "#browser-rename")

func cancel_rename() -> void:
	if _rename.is_empty(): return
	var old = _rename
	_rename = {}
	if host.call("has_element", "#browser-rename"):
		host.call("remove_element", "#browser-rename")
	if host.call("has_element", "#" + old.dom + " .name"):
		host.call("remove_attribute", "#" + old.dom + " .name", "hidden")

func commit_rename() -> void:
	if _rename.is_empty(): return
	var row = _rename.duplicate()
	var value = str(host.call("get_value", "#browser-rename")).strip_edges()
	cancel_rename()
	if value.is_empty() or value == row.label: return
	match row.kind:
		"event": rename_event(row.key, value)
		"folder": rename_folder(row.key, value)
		_: rename_item({"snapshot":"snapshots", "parameter":"globalParameters", "bank":"banks"}.get(row.kind, ""), row.key, value)

func _context(id: String) -> void:
	commit_rename()
	_menu_row = _rows.get(id, {})
	_menu.clear()
	var kind = _menu_row.get("kind", "")
	if kind == "event":
		_menu.add_item("Play", 0)
		_menu.add_item("Duplicate", 2)
	if kind in ["event", "folder", "snapshot", "parameter", "bank"]:
		_menu.add_item("Rename", 1)
		_menu.add_item("Delete", 3)
	if active_tab == "events":
		_menu.add_item("New event", 4)
		_menu.add_item("New folder", 5)
	elif active_tab == "assets":
		if kind == "asset": _menu.add_item("Preview", 6)
		_menu.add_item("Import lengths", 7)
		_menu.add_item("Rescan assets", 9)
	else: _menu.add_item("Add", 8)
	_menu.position = Vector2i(get_screen_position() + get_local_mouse_position())
	_menu.popup()

func _menu_action(action: int) -> void:
	var row = _menu_row
	match action:
		0:
			if server != null: server.StartEvent(find_event(row.key).path)
		1: begin_rename(row.dom)
		2: duplicate_event(row.key)
		3: _request_delete(row)
		4: new_event(row.key if row.get("kind") == "folder" else _folder())
		5: new_folder(row.key if row.get("kind") == "folder" else _folder())
		6: preview_asset(row.key)
		7: import_lengths()
		8: add_item()
		9: asset_root = scanner.scan(); rebuild()

func _request_delete(row: Dictionary) -> void:
	_pending = func():
		match row.kind:
			"event": remove_events([row.key])
			"folder": delete_folder(row.key)
			_: delete_item({"snapshot":"snapshots", "parameter":"globalParameters", "bank":"banks"}.get(row.kind, ""), row.key)
	_confirm.dialog_text = 'Delete "%s"?%s' % [row.label, " References to this parameter will report validation errors." if row.kind == "parameter" else ""]
	_confirm.popup_centered()

func handle(command: int) -> bool:
	match command:
		Commands.Command.ESCAPE:
			if not _rename.is_empty(): cancel_rename(); return true
		Commands.Command.DELETE:
			if _rename.is_empty() and _rows.has(_selected_row): _request_delete(_rows[_selected_row]); return true
		Commands.Command.DUPLICATE:
			if selection.kind == Selection.Kind.EVENT: duplicate_event(selection.id); return true
		Commands.Command.COPY:
			var event = find_event(selection.id)
			if not event.is_empty(): _clipboard = event.duplicate(true); return true
		Commands.Command.PASTE:
			if not _clipboard.is_empty():
				var copy = _clipboard.duplicate(true)
				Cloner.reassign_ids(copy)
				copy.path = TreeModel.unique_path(document.project.events, TreeModel.join(_folder(), TreeModel.name_of(copy.path)))
				document.transact("Paste event", func(): document.project.events.append(copy), document.ChangeScope.EVENTS, copy.id)
				_select_event(copy.id)
				return true
	return false

func find_event(id: String) -> Dictionary:
	return _find("events", id)

func _find(collection: String, id: String) -> Dictionary:
	for item in document.project.get(collection, []):
		if item.get("id") == id: return item
	return {}

func _folder() -> String:
	var event = find_event(selection.id) if selection.kind == Selection.Kind.EVENT else {}
	return TreeModel.folder_of(event.path) if not event.is_empty() else selected_folder

func _select_event(id: String) -> void:
	selection.select(Selection.Kind.EVENT, id)
	event_selected.emit(id)

func new_event(folder: String = "") -> String:
	var bus_id = Model.NONE
	for bus in document.project.mixer.buses:
		if bus.parent == Model.NONE: bus_id = bus.id; break
	var event = Model.create("AudioEvent", {"path": TreeModel.unique_path(document.project.events, TreeModel.join(folder, "New Event")), "bus": bus_id})
	event.tracks.append(Model.create("Track", {"name": "Track 1"}))
	empty_folders.erase(folder)
	collapsed.erase(folder)
	document.transact("New event", func(): document.project.events.append(event), document.ChangeScope.EVENTS, event.id)
	_select_event(event.id)
	if host != null: begin_rename(_selected_row)
	return event.id

func new_folder(parent: String = "") -> String:
	var desired = TreeModel.join(parent, "New Folder")
	var path = desired
	var suffix = 2
	while empty_folders.has(path) or document.project.events.any(func(event): return TreeModel.is_under(event.path, path) and event.path != path):
		path = desired + " " + str(suffix)
		suffix += 1
	empty_folders.append(path)
	collapsed.erase(parent)
	selected_folder = path
	selection.clear()
	rebuild()
	begin_rename(_selected_row)
	return path

func rename_event(id: String, name: String) -> void:
	var event = find_event(id)
	if event.is_empty() or not TreeModel.is_valid_name(name): return
	var path = TreeModel.join(TreeModel.folder_of(event.path), name.strip_edges())
	if path == event.path or document.project.events.any(func(item): return item.id != id and item.path == path): return
	document.transact("Rename event", func(): event.path = path, document.ChangeScope.EVENTS, id)

func rename_folder(folder: String, name: String) -> void:
	if not TreeModel.is_valid_name(name): return
	var new_path = TreeModel.join(TreeModel.folder_of(folder), name.strip_edges())
	if new_path == folder: return
	if empty_folders.has(folder): empty_folders.erase(folder); empty_folders.append(new_path)
	if collapsed.has(folder): collapsed.erase(folder); collapsed[new_path] = true
	selected_folder = new_path
	document.transact("Rename folder", func():
		for event in document.project.events:
			if event.path != folder and TreeModel.is_under(event.path, folder):
				event.path = TreeModel.reparent(event.path, folder, new_path), document.ChangeScope.EVENTS)

func move_event_to_folder(id: String, folder: String) -> void:
	var event = find_event(id)
	if event.is_empty(): return
	var path = TreeModel.join(folder, TreeModel.name_of(event.path))
	if path == event.path: return
	path = TreeModel.unique_path(document.project.events, path)
	empty_folders.erase(folder)
	document.transact("Move event", func(): event.path = path, document.ChangeScope.EVENTS, id)

func remove_events(ids: Array) -> void:
	document.transact("Delete events", func():
		document.project.events = document.project.events.filter(func(event): return not ids.has(event.id))
		for bank in document.project.banks:
			bank.events = bank.events.filter(func(id): return not ids.has(id)), document.ChangeScope.EVENTS)
	if ids.has(selection.id): selection.clear()

func delete_folder(folder: String) -> void:
	var ids: Array = []
	for event in document.project.events:
		if TreeModel.is_under(event.path, folder): ids.append(event.id)
	empty_folders.erase(folder)
	if not ids.is_empty(): remove_events(ids)
	else: rebuild()

func duplicate_event(id: String) -> String:
	var source = find_event(id)
	if source.is_empty(): return ""
	var copy = Cloner.clone(document.project, source, TreeModel.unique_path(document.project.events, source.path + " copy"))
	var index = document.project.events.find(source) + 1
	document.transact("Duplicate event", func(): document.project.events.insert(index, copy), document.ChangeScope.EVENTS, copy.id)
	_select_event(copy.id)
	return copy.id

func add_tag(id: String, raw: String) -> void:
	var tag = TreeModel.normalize_tag(raw)
	var event = find_event(id)
	if event.is_empty() or tag.is_empty() or event.tags.has(tag): return
	document.transact("Add tag", func(): event.tags.append(tag), document.ChangeScope.EVENTS, id)

func remove_tag(id: String, tag: String) -> void:
	var event = find_event(id)
	if event.is_empty() or not event.tags.has(tag): return
	document.transact("Remove tag", func(): event.tags.erase(tag), document.ChangeScope.EVENTS, id)

func add_item() -> void:
	if active_tab == "events": new_event(_folder()); return
	var kind = {"snapshots":"Snapshot", "parameters":"ParameterDefinition", "banks":"AudioBank"}.get(active_tab, "")
	if kind.is_empty(): return
	var label = {"snapshots":"New Snapshot", "parameters":"New Parameter", "banks":"New Bank"}[active_tab]
	var collection = _collection()
	var name = label
	var suffix = 2
	while document.project[collection].any(func(item): return item.name == name):
		name = label + " " + str(suffix)
		suffix += 1
	var item = Model.create(kind, {"name": name})
	var scope = {"snapshots":document.ChangeScope.SNAPSHOTS, "parameters":document.ChangeScope.GLOBAL_PARAMETERS, "banks":document.ChangeScope.EVENTS}[active_tab]
	document.transact("New " + active_tab.trim_suffix("s"), func(): document.project[collection].append(item), scope, item.id)
	if active_tab == "banks": selected_bank = item.id
	var selected_kind = {"snapshots":Selection.Kind.SNAPSHOT, "parameters":Selection.Kind.PARAMETER, "banks":Selection.Kind.BANK}[active_tab]
	selection.select(selected_kind, item.id)
	subject_selected.emit(selected_kind, item.id)
	begin_rename(_selected_row)

func rename_item(collection: String, id: String, name: String) -> void:
	var item = _find(collection, id)
	if item.is_empty() or name == item.name: return
	document.transact("Rename " + collection, func(): item.name = name, _scope(collection), id)

func delete_item(collection: String, id: String) -> void:
	var item = _find(collection, id)
	if item.is_empty(): return
	document.transact("Delete " + collection, func(): document.project[collection].erase(item), _scope(collection), id)
	if selection.id == id: selection.clear()
	if selected_bank == id: selected_bank = ""

func _scope(collection: String) -> int:
	return {"snapshots":document.ChangeScope.SNAPSHOTS, "globalParameters":document.ChangeScope.GLOBAL_PARAMETERS}.get(collection, document.ChangeScope.EVENTS)

func toggle_bank_membership(bank_id: String, event_id: String) -> void:
	var bank = _find("banks", bank_id)
	if bank.is_empty() or find_event(event_id).is_empty(): return
	document.transact("Toggle bank membership", func():
		if bank.events.has(event_id): bank.events.erase(event_id)
		else: bank.events.append(event_id), document.ChangeScope.EVENTS, bank_id)

func preview_asset(path: String) -> void:
	if _preview.playing and _preview.stream != null and _preview.stream.resource_path == path:
		_preview.stop()
		return
	if ResourceLoader.exists(path):
		var stream = ResourceLoader.load(path)
		if stream is AudioStream:
			_preview.stream = stream
			_preview.play()

static func referenced_assets(project: Dictionary) -> Array[String]:
	var paths: Array[String] = []
	for event in project.get("events", []):
		for track in event.get("tracks", []):
			for instrument in track.get("instruments", []):
				var candidates: Array = []
				if instrument.has("asset"): candidates.append(instrument.asset)
				for entry in instrument.get("entries", []): candidates.append(entry.get("asset", ""))
				for asset in candidates:
					var path = str(asset.get("path", "")) if asset is Dictionary else str(asset)
					if not path.is_empty() and not paths.has(path): paths.append(path)
	return paths

func import_lengths() -> void:
	var lengths: Dictionary = {}
	for path in referenced_assets(document.project):
		var seconds = scanner.length(path)
		if seconds > 0.0 and document.project.assetLengths.get(path, -1.0) != seconds:
			lengths[path] = seconds
	if lengths.is_empty():
		if host != null:
			host.call("set_text", "#status", "Asset lengths already up to date (%d referenced)" % referenced_assets(document.project).size())
		return
	document.transact("Import asset lengths", func(): document.project.assetLengths.merge(lengths, true), document.ChangeScope.ASSETS)

func _get_drag(at: Vector2) -> Variant:
	commit_rename()
	var id = str(host.call("element_from_point", at.x, at.y)).trim_suffix("-twisty")
	var row = _rows.get(id, {})
	if row.get("kind") not in ["event", "asset"]: return null
	var preview = Label.new()
	preview.text = row.label
	host.set_drag_preview(preview)
	return {"type": row.kind, "id": row.key, "path": row.key if row.kind == "asset" else find_event(row.key).path}

func _can_drop(_at: Vector2, data: Variant) -> bool:
	return active_tab == "events" and data is Dictionary and data.get("type") == "event" and not find_event(data.get("id", "")).is_empty()

func _drop(at: Vector2, data: Variant) -> void:
	var id = str(host.call("element_from_point", at.x, at.y)).trim_suffix("-twisty")
	var row = _rows.get(id, {})
	var folder = row.get("key", "") if row.get("kind") == "folder" else TreeModel.folder_of(find_event(row.get("key", "")).get("path", ""))
	move_event_to_folder(data.id, folder)

func _notification(what: int) -> void:
	if what == NOTIFICATION_DRAG_END and host != null:
		host.call("set_input_enabled", false)
		host.call("set_input_enabled", true)

func _gui_input(event: InputEvent) -> void:
	if host == null or not event is InputEventMouseButton:
		return
	if event.button_index == MOUSE_BUTTON_RIGHT and event.pressed:
		var point = host.get_local_mouse_position()
		var id = str(host.call("element_from_point", point.x, point.y)).trim_suffix("-twisty")
		_context(id)
		accept_event()
