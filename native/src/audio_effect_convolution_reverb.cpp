#include "audio_effect_convolution_reverb.h"

#include <godot_cpp/classes/audio_server.hpp>
#include <godot_cpp/classes/audio_stream_playback.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/core/math.hpp>
#include <godot_cpp/variant/packed_vector2_array.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

#include <algorithm>
#include <cmath>
#include <cstring>

namespace dirge {

using godot::AudioEffectInstance;
using godot::AudioFrame;
using godot::AudioServer;
using godot::AudioStream;
using godot::AudioStreamPlayback;
using godot::ClassDB;
using godot::D_METHOD;
using godot::PackedVector2Array;
using godot::PropertyInfo;
using godot::Ref;
using godot::Variant;
using godot::Vector2;

namespace {

constexpr float SILENCE_DB = -80.0f;
constexpr float TRIM_THRESHOLD = 1.0e-6f; // -120 dBFS
constexpr int32_t DECODE_CHUNK = 1024;

float db_to_gain(float p_db) {
	return p_db <= SILENCE_DB ? 0.0f : godot::Math::db_to_linear(p_db);
}

float server_mix_rate() {
	AudioServer *server = AudioServer::get_singleton();
	const float rate = server != nullptr ? server->get_mix_rate() : 0.0f;
	return rate > 0.0f ? rate : 44100.0f;
}

// Decodes `stream` at the server mix rate into interleaved-by-array L/R.
bool decode_impulse(const Ref<AudioStream> &stream, uint32_t max_frames, std::vector<float> &left, std::vector<float> &right) {
	Ref<AudioStreamPlayback> playback = stream->instantiate_playback();
	if (playback.is_null()) {
		return false;
	}
	const double length = stream->get_length();
	uint32_t target = max_frames;
	if (length > 0.0) {
		const double frames = std::ceil(length * static_cast<double>(server_mix_rate()));
		target = std::min<uint32_t>(max_frames, static_cast<uint32_t>(std::max(frames, 1.0)));
	}
	left.reserve(target);
	right.reserve(target);
	playback->start(0.0);
	while (left.size() < target) {
		const int32_t want = static_cast<int32_t>(std::min<uint32_t>(DECODE_CHUNK, target - static_cast<uint32_t>(left.size())));
		const PackedVector2Array chunk = playback->mix_audio(1.0f, want);
		const int64_t got = chunk.size();
		if (got <= 0) {
			break;
		}
		const Vector2 *data = chunk.ptr();
		for (int64_t i = 0; i < got; i++) {
			left.push_back(data[i].x);
			right.push_back(data[i].y);
		}
		if (!playback->is_playing()) {
			break;
		}
	}
	playback->stop();

	// Trim trailing silence so short impulses cost one partition, not MAX_SECONDS.
	size_t end = left.size();
	while (end > 0 && std::fabs(left[end - 1]) < TRIM_THRESHOLD && std::fabs(right[end - 1]) < TRIM_THRESHOLD) {
		end--;
	}
	left.resize(end);
	right.resize(end);
	return end > 0;
}

std::shared_ptr<const ConvolutionKernel> build_kernel(const Ref<AudioStream> &stream) {
	constexpr uint32_t BLOCK = DirgeAudioEffectConvolutionReverb::BLOCK;
	constexpr uint32_t FFT_SIZE = DirgeAudioEffectConvolutionReverb::FFT_SIZE;
	constexpr uint32_t BINS = DirgeAudioEffectConvolutionReverb::BINS;

	const uint32_t max_frames = static_cast<uint32_t>(DirgeAudioEffectConvolutionReverb::MAX_SECONDS * server_mix_rate());
	std::vector<float> left;
	std::vector<float> right;
	if (!decode_impulse(stream, max_frames, left, right)) {
		godot::UtilityFunctions::push_warning("DirgeAudioEffectConvolutionReverb: impulse stream decoded to silence; reverb disabled.");
		return nullptr;
	}

	bool stereo = false;
	for (size_t i = 0; i < left.size() && !stereo; i++) {
		stereo = std::fabs(left[i] - right[i]) > 1.0e-7f;
	}

	auto kernel = std::make_shared<ConvolutionKernel>();
	kernel->channels = stereo ? 2u : 1u;
	kernel->partitions = static_cast<uint32_t>((left.size() + BLOCK - 1) / BLOCK);

	Fft fft(FFT_SIZE);
	std::vector<float> re(FFT_SIZE);
	std::vector<float> im(FFT_SIZE);
	for (uint32_t c = 0; c < kernel->channels; c++) {
		const std::vector<float> &ir = c == 0 ? left : right;
		kernel->re[c].assign(static_cast<size_t>(kernel->partitions) * BINS, 0.0f);
		kernel->im[c].assign(static_cast<size_t>(kernel->partitions) * BINS, 0.0f);
		for (uint32_t p = 0; p < kernel->partitions; p++) {
			std::fill(re.begin(), re.end(), 0.0f);
			std::fill(im.begin(), im.end(), 0.0f);
			const size_t offset = static_cast<size_t>(p) * BLOCK;
			const size_t count = std::min<size_t>(BLOCK, ir.size() - offset);
			std::memcpy(re.data(), ir.data() + offset, count * sizeof(float));
			fft.forward(re.data(), im.data());
			std::memcpy(kernel->re[c].data() + static_cast<size_t>(p) * BINS, re.data(), BINS * sizeof(float));
			std::memcpy(kernel->im[c].data() + static_cast<size_t>(p) * BINS, im.data(), BINS * sizeof(float));
		}
	}
	return kernel;
}

} // namespace

void DirgeAudioEffectConvolutionReverb::_bind_methods() {
	ClassDB::bind_method(D_METHOD("set_impulse", "stream"), &DirgeAudioEffectConvolutionReverb::set_impulse);
	ClassDB::bind_method(D_METHOD("get_impulse"), &DirgeAudioEffectConvolutionReverb::get_impulse);
	ClassDB::bind_method(D_METHOD("set_wet_db", "wet_db"), &DirgeAudioEffectConvolutionReverb::set_wet_db);
	ClassDB::bind_method(D_METHOD("get_wet_db"), &DirgeAudioEffectConvolutionReverb::get_wet_db);
	ClassDB::bind_method(D_METHOD("set_dry_db", "dry_db"), &DirgeAudioEffectConvolutionReverb::set_dry_db);
	ClassDB::bind_method(D_METHOD("get_dry_db"), &DirgeAudioEffectConvolutionReverb::get_dry_db);
	ClassDB::bind_method(D_METHOD("set_gain_db", "gain_db"), &DirgeAudioEffectConvolutionReverb::set_gain_db);
	ClassDB::bind_method(D_METHOD("get_gain_db"), &DirgeAudioEffectConvolutionReverb::get_gain_db);

	ADD_PROPERTY(PropertyInfo(Variant::OBJECT, "impulse", godot::PROPERTY_HINT_RESOURCE_TYPE, "AudioStream"), "set_impulse", "get_impulse");
	ADD_PROPERTY(PropertyInfo(Variant::FLOAT, "wet_db", godot::PROPERTY_HINT_RANGE, "-80,12,0.1,suffix:dB"), "set_wet_db", "get_wet_db");
	ADD_PROPERTY(PropertyInfo(Variant::FLOAT, "dry_db", godot::PROPERTY_HINT_RANGE, "-80,12,0.1,suffix:dB"), "set_dry_db", "get_dry_db");
	ADD_PROPERTY(PropertyInfo(Variant::FLOAT, "gain_db", godot::PROPERTY_HINT_RANGE, "-80,24,0.1,suffix:dB"), "set_gain_db", "get_gain_db");
}

Ref<AudioEffectInstance> DirgeAudioEffectConvolutionReverb::_instantiate() {
	Ref<DirgeAudioEffectConvolutionReverbInstance> instance;
	instance.instantiate();
	instance->base = Ref<DirgeAudioEffectConvolutionReverb>(this);
	instance->configure(server_mix_rate());
	return instance;
}

void DirgeAudioEffectConvolutionReverb::set_impulse(const Ref<AudioStream> &p_stream) {
	impulse = p_stream;
	rebuild_kernel();
}

void DirgeAudioEffectConvolutionReverb::rebuild_kernel() {
	std::shared_ptr<const ConvolutionKernel> built;
	if (impulse.is_valid()) {
		built = build_kernel(impulse);
	}
	std::lock_guard<std::mutex> guard(kernel_mutex);
	kernel = std::move(built);
}

void DirgeAudioEffectConvolutionReverb::set_wet_db(float p_db) {
	wet_db = p_db;
	wet_linear.store(db_to_gain(p_db), std::memory_order_relaxed);
}

void DirgeAudioEffectConvolutionReverb::set_dry_db(float p_db) {
	dry_db = p_db;
	dry_linear.store(db_to_gain(p_db), std::memory_order_relaxed);
}

void DirgeAudioEffectConvolutionReverb::set_gain_db(float p_db) {
	gain_db = p_db;
	gain_linear.store(db_to_gain(p_db), std::memory_order_relaxed);
}

void DirgeAudioEffectConvolutionReverbInstance::configure(float p_mix_rate) {
	const uint32_t max_frames = static_cast<uint32_t>(DirgeAudioEffectConvolutionReverb::MAX_SECONDS * p_mix_rate);
	max_partitions = std::max<uint32_t>(1, (max_frames + BLOCK - 1) / BLOCK);
	for (Channel &ch : channels) {
		ch.fdl_re.assign(static_cast<size_t>(max_partitions) * BINS, 0.0f);
		ch.fdl_im.assign(static_cast<size_t>(max_partitions) * BINS, 0.0f);
	}
}

void DirgeAudioEffectConvolutionReverbInstance::refresh_kernel() {
	// Never block the audio thread: if the main thread is swapping the kernel
	// right now, keep the current one for this step and retry next time.
	if (!base->kernel_mutex.try_lock()) {
		return;
	}
	const bool changed = base->kernel.get() != kernel.get();
	if (changed) {
		kernel = base->kernel;
	}
	base->kernel_mutex.unlock();
	if (changed) {
		active_partitions = kernel ? std::min(kernel->partitions, max_partitions) : 0;
		fdl_pos = 0;
		fdl_valid = 0;
	}
}

void DirgeAudioEffectConvolutionReverbInstance::convolve_block() {
	const ConvolutionKernel &k = *kernel;
	const uint32_t partitions = active_partitions;
	const uint32_t history = std::min(fdl_valid + 1, partitions);

	for (uint32_t c = 0; c < 2; c++) {
		Channel &ch = channels[c];
		const uint32_t kc = std::min(c, k.channels - 1);

		// Overlap-save input: previous block followed by the new one.
		std::memcpy(work_re, ch.previous, BLOCK * sizeof(float));
		std::memcpy(work_re + BLOCK, ch.input, BLOCK * sizeof(float));
		std::memcpy(ch.previous, ch.input, BLOCK * sizeof(float));
		std::memset(work_im, 0, sizeof(work_im));
		fft.forward(work_re, work_im);

		float *slot_re = ch.fdl_re.data() + static_cast<size_t>(fdl_pos) * BINS;
		float *slot_im = ch.fdl_im.data() + static_cast<size_t>(fdl_pos) * BINS;
		std::memcpy(slot_re, work_re, BINS * sizeof(float));
		std::memcpy(slot_im, work_im, BINS * sizeof(float));

		std::memset(acc_re, 0, sizeof(acc_re));
		std::memset(acc_im, 0, sizeof(acc_im));
		for (uint32_t p = 0; p < history; p++) {
			const uint32_t slot = (fdl_pos + partitions - p) % partitions;
			const float *xr = ch.fdl_re.data() + static_cast<size_t>(slot) * BINS;
			const float *xi = ch.fdl_im.data() + static_cast<size_t>(slot) * BINS;
			const float *hr = k.re[kc].data() + static_cast<size_t>(p) * BINS;
			const float *hi = k.im[kc].data() + static_cast<size_t>(p) * BINS;
			for (uint32_t b = 0; b < BINS; b++) {
				acc_re[b] += xr[b] * hr[b] - xi[b] * hi[b];
				acc_im[b] += xr[b] * hi[b] + xi[b] * hr[b];
			}
		}

		// Rebuild the Hermitian spectrum and go back to time domain.
		std::memcpy(work_re, acc_re, BINS * sizeof(float));
		std::memcpy(work_im, acc_im, BINS * sizeof(float));
		for (uint32_t b = 1; b < BLOCK; b++) {
			work_re[FFT_SIZE - b] = acc_re[b];
			work_im[FFT_SIZE - b] = -acc_im[b];
		}
		fft.inverse(work_re, work_im);

		// The last BLOCK samples are the valid (non-aliased) output.
		for (uint32_t i = 0; i < BLOCK; i++) {
			ch.output[(ch.out_read + ch.out_count + i) % (2 * BLOCK)] = work_re[BLOCK + i];
		}
		ch.out_count += BLOCK;
	}

	fdl_pos = (fdl_pos + 1) % partitions;
	fdl_valid = std::min(fdl_valid + 1, partitions);
}

void DirgeAudioEffectConvolutionReverbInstance::_process(const void *p_src_buffer, AudioFrame *r_dst_buffer, int32_t p_frame_count) {
	const AudioFrame *src = static_cast<const AudioFrame *>(p_src_buffer);
	const uint32_t count = p_frame_count > 0 ? static_cast<uint32_t>(p_frame_count) : 0;

	refresh_kernel();
	const float dry = base->dry_linear.load(std::memory_order_relaxed);
	const float wet = base->wet_linear.load(std::memory_order_relaxed);
	const float gain = base->gain_linear.load(std::memory_order_relaxed);

	if (!kernel || active_partitions == 0) {
		const float g = dry * gain;
		for (uint32_t i = 0; i < count; i++) {
			r_dst_buffer[i].left = src[i].left * g;
			r_dst_buffer[i].right = src[i].right * g;
		}
		return;
	}

	Channel &left = channels[0];
	Channel &right = channels[1];
	for (uint32_t i = 0; i < count; i++) {
		left.input[fill] = src[i].left;
		right.input[fill] = src[i].right;
		fill++;
		if (fill == BLOCK) {
			convolve_block();
			fill = 0;
		}

		float wet_l = 0.0f;
		float wet_r = 0.0f;
		if (left.out_count > 0) {
			wet_l = left.output[left.out_read];
			left.out_read = (left.out_read + 1) % (2 * BLOCK);
			left.out_count--;
		}
		if (right.out_count > 0) {
			wet_r = right.output[right.out_read];
			right.out_read = (right.out_read + 1) % (2 * BLOCK);
			right.out_count--;
		}
		r_dst_buffer[i].left = (src[i].left * dry + wet_l * wet) * gain;
		r_dst_buffer[i].right = (src[i].right * dry + wet_r * wet) * gain;
	}
}

} // namespace dirge
