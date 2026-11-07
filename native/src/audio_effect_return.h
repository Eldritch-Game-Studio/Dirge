// Dirge native — DirgeAudioEffectReturn: receives every DirgeAudioEffectSend
// whose `target_bus` equals the name of the bus this effect sits on.
//
// Ordering: AudioServer processes buses from the highest index down to the
// lowest, so the return bus must have a LOWER index than all of its senders
// (exactly like Godot's own bus sends). With the opposite ordering the sent
// frames are consumed one mix step late instead of being lost.
//
// Bus activity: the engine drops the output of a bus that has no playing source
// of its own ("inactive" channel), even when effects wrote into it. A return bus
// usually has no sources, so this effect keeps its bus active by parking a
// silent AudioStreamPlayer (`DirgeReturnKeepAlive_<bus>`, an AudioStreamGenerator
// that never receives frames) under the scene root. It is created deferred on
// the main thread when the effect is instantiated and whenever the bus it sits
// on changes name.
#pragma once

#include "bus_locator.h"
#include "send_bus_registry.h"

#include <godot_cpp/classes/audio_effect.hpp>
#include <godot_cpp/classes/audio_effect_instance.hpp>
#include <godot_cpp/classes/ref.hpp>
#include <godot_cpp/variant/string.hpp>

#include <atomic>

namespace dirge {

class DirgeAudioEffectReturn : public godot::AudioEffect {
	GDCLASS(DirgeAudioEffectReturn, godot::AudioEffect)

	friend class DirgeAudioEffectReturnInstance;

	std::atomic<bool> keep_alive_pending{ false };

	// Main thread (deferred). Finds the bus this effect sits on and parks the
	// keep-alive player for it.
	void _ensure_keep_alive();
	void request_keep_alive();

protected:
	static void _bind_methods();

public:
	godot::Ref<godot::AudioEffectInstance> _instantiate() override;
};

class DirgeAudioEffectReturnInstance : public godot::AudioEffectInstance {
	GDCLASS(DirgeAudioEffectReturnInstance, godot::AudioEffectInstance)

	friend class DirgeAudioEffectReturn;

	godot::Ref<DirgeAudioEffectReturn> base;
	BusLocation location;
	godot::String bus_name;
	SendBus *source = nullptr;

protected:
	static void _bind_methods() {}

public:
	void _process(const void *p_src_buffer, godot::AudioFrame *r_dst_buffer, int32_t p_frame_count) override;
	// Returns have to run even when their bus received no direct input.
	bool _process_silence() const override { return true; }
};

} // namespace dirge
