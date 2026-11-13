@tool
extends RefCounted

enum Command { UNDO, REDO, SAVE, PLAY_STOP, DELETE, COPY, PASTE, DUPLICATE, SELECT_ALL, ESCAPE }

static func from_key(event: InputEventKey) -> int:
	if not event.pressed or event.echo:
		return -1
	var command = event.ctrl_pressed or event.meta_pressed
	if command:
		match event.keycode:
			KEY_Z: return Command.REDO if event.shift_pressed else Command.UNDO
			KEY_Y: return Command.REDO
			KEY_S: return Command.SAVE
			KEY_C: return Command.COPY
			KEY_V: return Command.PASTE
			KEY_D: return Command.DUPLICATE
			KEY_A: return Command.SELECT_ALL
	match event.keycode:
		KEY_SPACE: return Command.PLAY_STOP
		KEY_DELETE, KEY_BACKSPACE: return Command.DELETE
		KEY_ESCAPE: return Command.ESCAPE
	return -1
