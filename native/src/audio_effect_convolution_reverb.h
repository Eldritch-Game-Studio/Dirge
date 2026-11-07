// Dirge native — DirgeAudioEffectConvolutionReverb: uniformly partitioned
// frequency-domain convolution with an AudioStream impulse response.
//
// Setup (main thread, on `impulse` change): the stream is decoded through its
// AudioStreamPlayback at the server mix rate (any AudioStream works; WAV mono
// and stereo are the intended inputs), trailing silence is trimmed, the response
// is capped at MAX_SECONDS and split into BLOCK-sample partitions whose spectra
// are stored once. A 2-channel impulse convolves L with its left and R with its
// right response; a mono impulse is applied to both channels (dual mono).
//
// Processing (audio thread): overlap-save with a frequency-domain delay line; one
// forward and one inverse FFT per BLOCK samples per channel, plus one complex
// multiply-accumulate per partition and bin. No allocation after setup; a new
// kernel is picked up with a non-blocking try_lock.
#pragma once

#include "fft.h"

#include <godot_cpp/classes/audio_effect.hpp>
#include <godot_cpp/classes/audio_effect_instance.hpp>
#include <godot_cpp/classes/audio_stream.hpp>
#include <godot_cpp/classes/ref.hpp>

#include <atomic>
#include <memory>
#include <mutex>
#include <vector>

namespace dirge {

struct ConvolutionKernel {
	uint32_t partitions = 0; // number of BLOCK-sized partitions
	uint32_t channels = 0; // 1 = dual mono, 2 = stereo
	// partitions * BINS spectra per channel.
	std::vector<float> re[2];
	std::vector<float> im[2];
};

class DirgeAudioEffectConvolutionReverb : public godot::AudioEffect {
	GDCLASS(DirgeAudioEffectConvolutionReverb, godot::AudioEffect)

	friend class DirgeAudioEffectConvolutionReverbInstance;

public:
	static constexpr uint32_t BLOCK = 256;
	static constexpr uint32_t FFT_SIZE = 2 * BLOCK;
	static constexpr uint32_t BINS = BLOCK + 1;
	static constexpr float MAX_SECONDS = 4.0f;

private:
	godot::Ref<godot::AudioStream> impulse;
	float wet_db = -6.0f;
	float dry_db = 0.0f;
	float gain_db = 0.0f;

	std::atomic<float> wet_linear{ 0.5f };
	std::atomic<float> dry_linear{ 1.0f };
	std::atomic<float> gain_linear{ 1.0f };

	std::mutex kernel_mutex;
	std::shared_ptr<const ConvolutionKernel> kernel;

	void rebuild_kernel();

protected:
	static void _bind_methods();

public:
	godot::Ref<godot::AudioEffectInstance> _instantiate() override;

	void set_impulse(const godot::Ref<godot::AudioStream> &p_stream);
	godot::Ref<godot::AudioStream> get_impulse() const { return impulse; }

	void set_wet_db(float p_db);
	float get_wet_db() const { return wet_db; }

	void set_dry_db(float p_db);
	float get_dry_db() const { return dry_db; }

	void set_gain_db(float p_db);
	float get_gain_db() const { return gain_db; }
};

class DirgeAudioEffectConvolutionReverbInstance : public godot::AudioEffectInstance {
	GDCLASS(DirgeAudioEffectConvolutionReverbInstance, godot::AudioEffectInstance)

	friend class DirgeAudioEffectConvolutionReverb;

	static constexpr uint32_t BLOCK = DirgeAudioEffectConvolutionReverb::BLOCK;
	static constexpr uint32_t FFT_SIZE = DirgeAudioEffectConvolutionReverb::FFT_SIZE;
	static constexpr uint32_t BINS = DirgeAudioEffectConvolutionReverb::BINS;

	struct Channel {
		float input[BLOCK] = {};
		float previous[BLOCK] = {};
		float output[2 * BLOCK] = {};
		uint32_t out_read = 0;
		uint32_t out_count = 0;
		// Frequency-domain delay line: max_partitions * BINS.
		std::vector<float> fdl_re;
		std::vector<float> fdl_im;
	};

	godot::Ref<DirgeAudioEffectConvolutionReverb> base;
	std::shared_ptr<const ConvolutionKernel> kernel;
	Fft fft{ FFT_SIZE };
	uint32_t max_partitions = 0;
	uint32_t active_partitions = 0;
	uint32_t fill = 0;
	uint32_t fdl_pos = 0;
	uint32_t fdl_valid = 0;
	Channel channels[2];
	float work_re[FFT_SIZE] = {};
	float work_im[FFT_SIZE] = {};
	float acc_re[BINS] = {};
	float acc_im[BINS] = {};

	// Main thread, once: sizes the delay lines for MAX_SECONDS at the mix rate.
	void configure(float p_mix_rate);
	void refresh_kernel();
	void convolve_block();

protected:
	static void _bind_methods() {}

public:
	void _process(const void *p_src_buffer, godot::AudioFrame *r_dst_buffer, int32_t p_frame_count) override;
	bool _process_silence() const override { return true; }
};

} // namespace dirge
