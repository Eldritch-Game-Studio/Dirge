extends SceneTree
## Exit 0: no errors; 1: validator errors; 2: usage/read/parse failure.
const ProjectJson = preload("res://addons/dirge/model/Serialization/project_json.gd")
const Validator = preload("res://addons/dirge/model/Validation/project_validator.gd")

func _initialize() -> void:
	var path = ""
	var as_json = false
	for argument in OS.get_cmdline_user_args():
		if argument in ["-h", "--help"]:
			_usage()
			quit(0)
			return
		elif argument == "--json":
			as_json = true
		elif argument.begins_with("--") or not path.is_empty():
			printerr("dirge-validate: unexpected argument '%s'" % argument)
			_usage()
			quit(2)
			return
		else:
			path = argument
	if path.is_empty():
		_usage()
		quit(2)
		return
	var project = ProjectJson.load_file(path)
	if not ProjectJson.last_error.is_empty():
		printerr("dirge-validate: cannot load '%s': %s" % [path, ProjectJson.last_error])
		quit(2)
		return
	var diagnostics = Validator.validate(project)
	var errors = 0
	var warnings = 0
	var output = []
	for diagnostic in diagnostics:
		var severity = str(diagnostic.get("severity", "error")).to_lower()
		if severity == "error": errors += 1
		else: warnings += 1
		var subject = diagnostic.get("subject")
		if subject == "" or subject == "00000000000000000000000000000000": subject = null
		output.append({"severity": severity, "code": diagnostic.code, "subject": subject, "message": diagnostic.message})
		if not as_json:
			print("%s %s %s: %s" % [severity, diagnostic.code, "project" if subject == null else str(subject), diagnostic.message])
	if as_json:
		print(JSON.stringify(output, "  "))
	else:
		print('%s: "%s" — %d error(s), %d warning(s)' % [path, project.get("name", ""), errors, warnings])
	quit(0 if errors == 0 else 1)

func _usage() -> void:
	print("usage: scripts/validate.sh <file.dirge.json> [--json]")
	print("Exit 0: no errors. Exit 1: errors found. Exit 2: usage or parse failure.")
