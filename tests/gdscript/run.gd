extends SceneTree
## Execute behavior against the actual GDScript modules, not their source text.
var assertions: int = 0
var failures: Array[String] = []
var current_suite: String = ""
var suites: Array[Dictionary] = []

func _initialize() -> void:
	_run.call_deferred()

func check(condition: bool, message: String) -> void:
	assertions += 1
	if not condition:
		var failure = current_suite + ": " + message
		failures.append(failure)
		push_error(failure)

func near(actual: float, expected: float, tolerance: float, message: String) -> void:
	check(absf(actual - expected) <= tolerance, "%s (actual=%s expected=%s tolerance=%s)" % [message, actual, expected, tolerance])

func _run() -> void:
	check(not OS.has_feature("mono"), "qualification requires standard non-.NET Godot")
	var requested: PackedStringArray = []
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--suite="):
			requested.append(argument.trim_prefix("--suite="))
	var directory = DirAccess.open("res://tests/gdscript")
	if directory == null:
		check(false, "regression suite directory is missing")
	else:
		var names = directory.get_files()
		names.sort()
		for filename in names:
			if not filename.begins_with("test_") or not filename.ends_with(".gd"):
				continue
			if not requested.is_empty() and not requested.has(filename.get_basename().trim_prefix("test_")):
				continue
			current_suite = filename
			var start_count = assertions
			var start_failures = failures.size()
			var script = load("res://tests/gdscript/" + filename)
			if script == null or not script.can_instantiate():
				check(false, "suite failed to load")
				continue
			var suite = script.new()
			if not suite.has_method("run"):
				check(false, "suite has no run(test) method")
				continue
			var started = Time.get_ticks_usec()
			await suite.run(self)
			check(assertions > start_count, "suite exercised behavioral assertions")
			suites.append({"name": filename, "assertions": assertions - start_count, "failures": failures.size() - start_failures, "elapsed_ms": (Time.get_ticks_usec() - started) / 1000.0})
			print("SUITE ", JSON.stringify(suites.back()))
	current_suite = "runner"
	check(not suites.is_empty(), "at least one requested suite executed")
	var result = {"version": "1.1.0", "godot": Engine.get_version_info(), "mono": OS.has_feature("mono"), "assertions": assertions, "failures": failures, "suites": suites}
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--result="):
			var file = FileAccess.open(argument.trim_prefix("--result="), FileAccess.WRITE)
			if file != null:
				file.store_string(JSON.stringify(result, "\t") + "\n")
	print("DIRGE_TEST ", "PASS" if failures.is_empty() else "FAIL", " assertions=", assertions, " failures=", failures.size())
	quit(0 if failures.is_empty() else 1)
