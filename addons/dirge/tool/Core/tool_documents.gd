@tool
extends RefCounted
const Scale = preload("res://addons/dirge/tool/Core/tool_scale.gd")
const Palette = preload("res://addons/dirge/tool/Core/tool_theme.gd")

static func create(file_name: String) -> Control:
	assert(ClassDB.class_exists("DocumentHost"), "Dirge requires the NecronomiDOM native extension.")
	var host: Control = ClassDB.instantiate("DocumentHost")
	host.name = "Document"
	host.focus_mode = Control.FOCUS_ALL
	host.call("load_document", "res://addons/dirge/tool/ui/" + file_name)
	host.call("set_attribute", "html", "style", Palette.css_text())
	host.set_meta("dirge_document", true)
	return host

static func slot(host: Control, slot_name: String = "DocumentSlot", minimum: Vector2 = Vector2.ZERO) -> Control:
	var frame = Control.new()
	frame.name = slot_name
	frame.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	frame.size_flags_vertical = Control.SIZE_EXPAND_FILL
	frame.mouse_filter = Control.MOUSE_FILTER_PASS
	frame.set_meta("dirge_authored_minimum", minimum)
	frame.add_child(host)
	var resize = func():
		var scale_value = Scale.compute()
		frame.custom_minimum_size = frame.get_meta("dirge_authored_minimum", minimum) * scale_value
		host.position = Vector2.ZERO
		host.scale = Vector2.ONE * scale_value
		host.size = frame.size / scale_value
	frame.resized.connect(resize)
	frame.tree_entered.connect(resize)
	resize.call()
	return frame

static func refresh(root: Node) -> void:
	if root is Control:
		if root.has_meta("dirge_authored_minimum"):
			root.custom_minimum_size = root.get_meta("dirge_authored_minimum") * Scale.compute()
			root.resized.emit()
		if root.has_meta("dirge_document"):
			root.call("set_attribute", "html", "style", Palette.css_text())
		root.queue_redraw()
	for child in root.get_children():
		refresh(child)

static func escape(value: Variant) -> String:
	return str(value).xml_escape(true)

static func hidden(host: Object, selector: String, value: bool) -> void:
	if value:
		host.call("set_attribute", selector, "hidden", "")
	else:
		host.call("remove_attribute", selector, "hidden")

static func disabled(host: Object, selector: String, value: bool) -> void:
	if value:
		host.call("set_attribute", selector, "disabled", "")
	else:
		host.call("remove_attribute", selector, "disabled")

static func class_state(host: Object, selector: String, css_class: String, value: bool) -> void:
	host.call("add_class" if value else "remove_class", selector, css_class)
