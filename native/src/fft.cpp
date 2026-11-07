#include "fft.h"

#include <cmath>
#include <utility>

namespace dirge {

Fft::Fft(uint32_t size) :
		n(size), reverse(size), cos_table(size / 2), sin_table(size / 2) {
	uint32_t bits = 0;
	while ((1u << bits) < n) {
		bits++;
	}
	for (uint32_t i = 0; i < n; i++) {
		uint32_t r = 0;
		for (uint32_t b = 0; b < bits; b++) {
			r |= ((i >> b) & 1u) << (bits - 1 - b);
		}
		reverse[i] = r;
	}
	constexpr double two_pi = 6.283185307179586476925286766559;
	const double step = two_pi / static_cast<double>(n);
	for (uint32_t k = 0; k < n / 2; k++) {
		cos_table[k] = static_cast<float>(std::cos(step * k));
		sin_table[k] = static_cast<float>(std::sin(step * k));
	}
}

void Fft::forward(float *re, float *im) const {
	transform(re, im, false);
}

void Fft::inverse(float *re, float *im) const {
	transform(re, im, true);
	const float scale = 1.0f / static_cast<float>(n);
	for (uint32_t i = 0; i < n; i++) {
		re[i] *= scale;
		im[i] *= scale;
	}
}

void Fft::transform(float *re, float *im, bool invert) const {
	for (uint32_t i = 0; i < n; i++) {
		const uint32_t j = reverse[i];
		if (j > i) {
			std::swap(re[i], re[j]);
			std::swap(im[i], im[j]);
		}
	}
	const float sign = invert ? 1.0f : -1.0f;
	for (uint32_t len = 2; len <= n; len <<= 1) {
		const uint32_t half = len >> 1;
		const uint32_t stride = n / len;
		for (uint32_t start = 0; start < n; start += len) {
			for (uint32_t k = 0; k < half; k++) {
				const float wr = cos_table[k * stride];
				const float wi = sign * sin_table[k * stride];
				const uint32_t a = start + k;
				const uint32_t b = a + half;
				const float tr = re[b] * wr - im[b] * wi;
				const float ti = re[b] * wi + im[b] * wr;
				re[b] = re[a] - tr;
				im[b] = im[a] - ti;
				re[a] += tr;
				im[a] += ti;
			}
		}
	}
}

} // namespace dirge
