// Dirge native — iterative radix-2 complex FFT with precomputed tables.
// Split (re/im) arrays keep the inner loops vectorizable; no allocation after
// construction.
#pragma once

#include <cstdint>
#include <vector>

namespace dirge {

class Fft {
public:
	// `size` must be a power of two >= 2.
	explicit Fft(uint32_t size);

	uint32_t size() const { return n; }

	// In-place forward transform (no scaling).
	void forward(float *re, float *im) const;
	// In-place inverse transform, scaled by 1/size.
	void inverse(float *re, float *im) const;

private:
	void transform(float *re, float *im, bool invert) const;

	uint32_t n;
	std::vector<uint32_t> reverse;
	std::vector<float> cos_table; // n/2 entries: cos(2*pi*k/n)
	std::vector<float> sin_table; // n/2 entries: sin(2*pi*k/n)
};

} // namespace dirge
