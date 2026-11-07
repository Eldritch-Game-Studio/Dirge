#include "audio_effect_send.h"

#include <godot_cpp/classes/audio_server.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/core/math.hpp>

#include <cstring>

namespace dirge {

using godot::AudioEffectInstance;
using godot::AudioFrame;
using godot::AudioServer;
using godot::ClassDB;
using godot::D_METHOD;
using godot::PropertyInfo;
using godot::Ref;
using godot::String;
using godot::Variant;

namespace {

constexpr float SILENCE_DB = -80.0f;

float db_to_gain(float p_db) {
	return p_db <= SILENCE_DB ? 0.0f : godot::Math::db_to_linear(p_db);
}

} // namespace

void DirgeAudioEffectSend::_bind_methods() {
	ClassDB::bind_method(D_METHOD("set_target_bus", "bus"), &DirgeAudioEffectSend::set_target_bus);
	ClassDB::bind_method(D_METHOD("get_target_bus"), &DirgeAudioEffectSend::get_target_bus);
	ClassDB::bind_method(D_METHOD("set_level_db", "level_db"), &DirgeAudioEffectSend::set_level_db);
	ClassDB::bind_method(D_METHOD("get_level_db"), &DirgeAudioEffectSend::get_level_db);
	ClassDB::bind_method(D_METHOD("set_pre_fader", "enabled"), &DirgeAudioEffectSend::set_pre_fader);
	ClassDB::bind_method(D_METHOD("is_pre_fader"), &DirgeAudioEffectSend::is_pre_fader);

	ADD_PROPERTY(PropertyInfo(Variant::STRING, "target_bus"), "set_target_bus", "get_target_bus");
	ADD_PROPERTY(PropertyInfo(Variant::FLOAT, "level_db", godot::PROPERTY_HINT_RANGE, "-80,12,0.1,suffix:dB"), "set_level_db", "get_level_db");
	ADD_PROPERTY(PropertyInfo(Variant::BOOL, "pre_fader"), "set_pre_fader", "is_pre_fader");
}

Ref<AudioEffectInstance> DirgeAudioEffectSend::_instantiate() {
	Ref<DirgeAudioEffectSendInstance> instance;
	instance.instantiate();
	instance->base = Ref<DirgeAudioEffectSend>(this);
	return instance;
}

void DirgeAudioEffectSend::set_target_bus(const String &p_bus) {
	target_bus = p_bus;
	target.store(p_bus.is_empty() ? nullptr : SendBusRegistry::acquire(p_bus), std::memory_order_release);
}

void DirgeAudioEffectSend::set_level_db(float p_db) {
	level_db = p_db;
	level_linear.store(db_to_gain(p_db), std::memory_order_relaxed);
}

void DirgeAudioEffectSend::set_pre_fader(bool p_enabled) {
	pre_fader = p_enabled;
	pre_fader_live.store(p_enabled, std::memory_order_relaxed);
}

void DirgeAudioEffectSendInstance::_process(const void *p_src_buffer, AudioFrame *r_dst_buffer, int32_t p_frame_count) {
	const AudioFrame *src = static_cast<const AudioFrame *>(p_src_buffer);
	const uint32_t count = p_frame_count > 0 ? static_cast<uint32_t>(p_frame_count) : 0;
	std::memcpy(r_dst_buffer, src, count * sizeof(AudioFrame));

	SendBus *target = base->target.load(std::memory_order_acquire);
	if (target == nullptr || count == 0) {
		return;
	}
	if (!locate_instance(base.ptr(), this, location)) {
		return;
	}

	float gain = base->level_linear.load(std::memory_order_relaxed);
	if (!base->pre_fader_live.load(std::memory_order_relaxed)) {
		AudioServer *server = AudioServer::get_singleton();
		if (server->is_bus_mute(location.bus)) {
			return;
		}
		gain *= godot::Math::db_to_linear(server->get_bus_volume_db(location.bus));
	}
	if (gain <= 0.0f) {
		return;
	}
	target->add(static_cast<uint32_t>(location.channel), src, count, gain);
}

} // namespace dirge
