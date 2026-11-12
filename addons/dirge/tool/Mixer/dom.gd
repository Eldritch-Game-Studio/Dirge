@tool
extends RefCounted
## Small selector helpers; DocumentHost remains the sole retained DOM owner.
var host
var _shown: Dictionary = {}
func _init(document_host = null) -> void:
	host = document_host
static func escape(value) -> String:
	return str(value).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;").replace("'", "&#39;")
func text(selector: String, value) -> void:
	var key = selector + ":text"
	var content = str(value)
	if _shown.get(key) != content:
		host.set_text(selector, content)
		_shown[key] = content
func style(selector: String, property: String, value: String) -> void:
	var key = selector + ":style:" + property
	if _shown.get(key) != value:
		host.set_style(selector, property, value)
		_shown[key] = value
func value(selector: String, content) -> void:
	if host.get_active_element_id() == selector.trim_prefix("#"): return
	if host.get_value(selector) != str(content): host.set_value(selector, str(content))
func hidden(selector: String, hide: bool) -> void:
	var key = selector + ":hidden"
	if _shown.get(key) == hide: return
	if hide: host.set_attribute(selector, "hidden", "")
	else: host.remove_attribute(selector, "hidden")
	_shown[key] = hide
func toggle(selector: String, token: String, enabled: bool) -> void:
	if enabled and not host.has_class(selector, token): host.add_class(selector, token)
	elif not enabled and host.has_class(selector, token): host.remove_class(selector, token)
func enabled(selector: String, enabled_value: bool) -> void:
	if enabled_value: host.remove_attribute(selector, "disabled")
	else: host.set_attribute(selector, "disabled", "")
func forget() -> void:
	_shown.clear()
