// Dirge native — process-wide send/return registry.
//
// A DirgeAudioEffectSend adds its bus' frames into the SendBus named by its
// `target_bus`; the DirgeAudioEffectReturn sitting on the bus of that name mixes
// the accumulated frames into its own bus and clears them. Godot processes buses
// from the highest index to the lowest, so a return bus must have a LOWER index
// than every bus that sends to it (same rule as Godot's own bus sends).
#pragma once

#include <godot_cpp/classes/audio_frame.hpp>
#include <godot_cpp/variant/string.hpp>

#include <cstdint>

namespace dirge {

// One stereo pair of a named return target. AudioServer processes every bus with
// the same buffer size, so `pending` is the frame count of the current mix step.
struct SendChannel {
	static constexpr uint32_t CAPACITY = 8192;
	godot::AudioFrame frames[CAPACITY];
	uint32_t pending = 0;
};

struct SendBus {
	// Godot's maximum bus channel (stereo-pair) count; 7.1 uses four.
	static constexpr uint32_t MAX_CHANNELS = 4;
	SendChannel channels[MAX_CHANNELS];

	// Audio thread. Adds `count` frames scaled by `gain`.
	void add(uint32_t channel, const godot::AudioFrame *src, uint32_t count, float gain);
	// Audio thread. Sums the pending frames into `dst` (up to `count`) and clears
	// everything that was pending. Returns the number of frames mixed.
	uint32_t consume(uint32_t channel, godot::AudioFrame *dst, uint32_t count);
};

class SendBusRegistry {
public:
	// Returns the SendBus for `name`, creating it on first use. Entries live for
	// the whole process, so the returned pointer stays valid and may be cached by
	// the audio thread. Takes a mutex: call from the main thread or, on the audio
	// thread, only when a bus name actually changed.
	static SendBus *acquire(const godot::String &name);
};

} // namespace dirge
