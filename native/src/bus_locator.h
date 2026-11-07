// Dirge native — locate an effect instance inside the AudioServer bus layout.
//
// AudioEffectInstance has no knowledge of the bus it runs on; the engine only
// hands it buffers. Both Send and Return need their own bus (volume for post-fader
// sends, name for returns), so the instance caches (bus, effect, channel) and
// re-validates the cache every mix step with a single AudioServer lookup, doing a
// full rescan only when the layout changed under it. All AudioServer getters used
// here are lock-free index reads and allocate nothing.
#pragma once

#include <godot_cpp/classes/audio_effect.hpp>
#include <godot_cpp/classes/audio_effect_instance.hpp>

#include <cstdint>

namespace dirge {

struct BusLocation {
	int32_t bus = -1;
	int32_t effect = -1;
	int32_t channel = -1;

	bool valid() const { return bus >= 0; }
};

// Audio thread. Validates `loc` or rescans the layout. Returns false (and resets
// `loc`) when the instance is not currently part of any bus.
bool locate_instance(const godot::AudioEffect *effect, const godot::AudioEffectInstance *instance, BusLocation &loc);

// Main thread. Bus index of the first bus whose effect chain contains `effect`,
// or -1.
int32_t find_effect_bus(const godot::AudioEffect *effect);

} // namespace dirge
