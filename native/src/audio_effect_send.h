// Dirge native — DirgeAudioEffectSend: aux send to a named return bus.
//
// Passes its input through untouched and adds a copy, scaled by `level_db`, into
// the SendBus registry entry named `target_bus`. A DirgeAudioEffectReturn on the
// bus of that name mixes it in. Godot applies the bus fader AFTER the effect
// chain, so a post-fader send (`pre_fader == false`) multiplies by the current
// volume (and mute) of the bus it sits on; a pre-fader send ignores the fader.
#pragma once

#include "bus_locator.h"
#include "send_bus_registry.h"

#include <godot_cpp/classes/audio_effect.hpp>
#include <godot_cpp/classes/audio_effect_instance.hpp>
#include <godot_cpp/classes/ref.hpp>
#include <godot_cpp/variant/string.hpp>

#include <atomic>

namespace dirge {

class DirgeAudioEffectSend : public godot::AudioEffect {
	GDCLASS(DirgeAudioEffectSend, godot::AudioEffect)

	friend class DirgeAudioEffectSendInstance;

	godot::String target_bus;
	float level_db = 0.0f;
	bool pre_fader = false;

	// Read by the audio thread.
	std::atomic<SendBus *> target{ nullptr };
	std::atomic<float> level_linear{ 1.0f };
	std::atomic<bool> pre_fader_live{ false };

protected:
	static void _bind_methods();

public:
	godot::Ref<godot::AudioEffectInstance> _instantiate() override;

	void set_target_bus(const godot::String &p_bus);
	godot::String get_target_bus() const { return target_bus; }

	void set_level_db(float p_db);
	float get_level_db() const { return level_db; }

	void set_pre_fader(bool p_enabled);
	bool is_pre_fader() const { return pre_fader; }
};

class DirgeAudioEffectSendInstance : public godot::AudioEffectInstance {
	GDCLASS(DirgeAudioEffectSendInstance, godot::AudioEffectInstance)

	friend class DirgeAudioEffectSend;

	godot::Ref<DirgeAudioEffectSend> base;
	BusLocation location;

protected:
	static void _bind_methods() {}

public:
	void _process(const void *p_src_buffer, godot::AudioFrame *r_dst_buffer, int32_t p_frame_count) override;
	bool _process_silence() const override { return false; }
};

} // namespace dirge
