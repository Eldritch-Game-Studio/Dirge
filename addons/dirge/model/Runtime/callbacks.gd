@tool
extends RefCounted

const NONE = 0
const STARTED = 1
const STOPPED = 2
const TIMELINE_MARKER = 4
const TIMELINE_BEAT = 8
const SOUND_PLAYED = 16
const SOUND_STOPPED = 32
const VIRTUALIZED = 64
const UNVIRTUALIZED = 128
const STOLEN = 256
const PROGRAMMER_SOUND = 512
const ALL = 1023

const TYPES = {
	"StartedCallback": STARTED, "StoppedCallback": STOPPED,
	"MarkerCallback": TIMELINE_MARKER, "BeatCallback": TIMELINE_BEAT,
	"SoundPlayedCallback": SOUND_PLAYED, "SoundStoppedCallback": SOUND_STOPPED,
	"VirtualizedCallback": VIRTUALIZED, "UnvirtualizedCallback": UNVIRTUALIZED,
	"StolenCallback": STOLEN, "ProgrammerSoundCallback": PROGRAMMER_SOUND,
}
