/* ***************************************************************************
 * This file is part of the Redzen code library.
 * 
 * Copyright 2005-2018 Colin Green (colin.green1@gmail.com)
 *
 * Redzen is free software; you can redistribute it and/or modify
 * it under the terms of The MIT License (MIT).
 *
 * You should have received a copy of the MIT License
 * along with Redzen; if not, see https://opensource.org/licenses/MIT.
 */

using System;
using System.Runtime.CompilerServices;

namespace Redzen.Random
{
    /// <summary>
    /// A fast random number generator for .NET
    /// Colin Green, January 2005
    /// 
    /// 
    /// Key points:
    ///  1) Based on a simple and fast xor-shift pseudo random number generator (RNG) specified in: 
    ///  Marsaglia, George. (2003). Xorshift RNGs.
    ///  http://www.jstatsoft.org/v08/i14/paper
    ///  
    ///  This particular implementation of xorshift has a period of 2^128-1. See the above paper to see
    ///  how this can be easily extended if you need a longer period. At the time of writing I could find no 
    ///  information on the period of System.Random for comparison.
    /// 
    ///  2) Faster than System.Random. Up to 8x faster, depending on which methods are called.
    /// 
    ///  3) Direct replacement for System.Random. This class implements all of the methods that System.Random 
    ///  does plus some additional methods. The like named methods are functionally equivalent.
    ///  
    ///  4) Allows fast re-initialisation with a seed, unlike System.Random which accepts a seed at construction
    ///  time which then executes a relatively expensive initialisation routine. This provides a significant speed
    ///  improvement if you need to reset the pseudo-random number sequence many times, e.g. if you want to 
    ///  re-generate the same sequence of random numbers many times. An alternative might be to cache random numbers 
    ///  in an array, but that approach is limited by memory capacity and the fact that you may also want a large 
    ///  number of different sequences cached. Each sequence can be represented by a single seed value (int) when 
    ///  using this class.
    /// </summary>
    public sealed class XorShiftRandom : IRandomSource
    {
        // Constants.
        const double INCR_DOUBLE = 1.0 / (1UL << 32);
        const float INCR_FLOAT = 1f / (1U << 24);

        // RNG state.
        uint _x, _y, _z, _w;

        #region Constructors

        /// <summary>
        /// Initialises a new instance with a seed from the default seed source.
        /// </summary>
        public XorShiftRandom()
        {
            Reinitialise(RandomDefaults.GetSeed());
        }

        /// <summary>
        /// Initialises a new instance with the provided seed.
        /// </summary>
        public XorShiftRandom(ulong seed)
        {
            Reinitialise(seed);
        }

        #endregion

        #region Public Methods [Re-initialisation]

        /// <summary>
        /// Re-initialises the random number generator state using the provided seed.
        /// </summary>
        public void Reinitialise(ulong seed)
        {
            // Notes.
            // The first random sample will be very strongly correlated to the value of _x we set here; 
            // such a correlation is undesirable, therefore we significantly weaken it by hashing the 
            // seed's bits using the splitmix64 PRNG.
            //
            // It is required that at least one of the state variables be non-zero;
            // use of splitmix64 satisfies this requirement because it is an equidistributed generator,
            // thus if it outputs a zero it will next produce a zero after a further 2^64 outputs.

            // Use the splitmix64 RNG to hash the seed.
            ulong t = Splitmix64Rng.Next(ref seed);
            _x = (uint)t;
            _y = (uint)(t >> 32);

            t = Splitmix64Rng.Next(ref seed);
            _z = (uint)t;
            _w = (uint)(t >> 32);
        }

        #endregion

        #region Public Methods [System.Random functionally equivalent methods]

        /// <summary>
        /// Generate a random Int32 over the interval [0, Int32.MaxValue), i.e. exclusive of Int32.MaxValue.
        /// </summary>
        /// <remarks>
        /// Int32.MaxValue is excluded in order to be functionally equivalent with System.Random.Next().
        /// 
        /// For slightly improved performance consider these alternatives:
        /// 
        ///  * NextInt() returns an Int32 over the interval [0 to Int32.MaxValue], i.e. inclusive of Int32.MaxValue.
        /// 
        ///  * NextUInt(). Cast the result to an Int32 to generate an value over the full range of an Int32,
        ///    including negative values.
        /// </remarks>
        public int Next()
        {
            // Perform rejection sampling to handle the special case where the value int.MaxValue is generated;
            // this value is outside the range of permitted values for this method. 
            // Rejection sampling ensures we produce an unbiased sample.
            uint rtn;
            do
            { 
                rtn = NextInner() & 0x7fff_ffff;
            }
            while(rtn == 0x7fff_ffff);
                
            return (int)rtn;            
        }

        /// <summary>
        /// Generate a random Int32 over the interval [0 to maxValue), i.e. excluding maxValue.
        /// </summary>
        public int Next(int maxValue)
        {
            if (maxValue < 0) {
                throw new ArgumentOutOfRangeException(nameof(maxValue), maxValue, "maxValue must be >= 0");
            }

            // Notes. 
            // We resort to floating point multiplication to obtain an Int32 in the required range, casting the
            // result back to an integer. This approach is able to generate all possible integers in the range,
            // and without bias, because a double precision float has 53 bits of precision far in excess of the 
            // minimum requirement of 32 bits.
            // 
            // In principle using floating point arithmetic operating on 64 bits is slower than integer arithmetic
            // but this is likely the fastest method if hardware floating point is available.
            //
            // The (or an) integer arithmetic approach is as follows:
            //
            //   1) Generate N bits such that maxValue <= 2^N.
            //   2) Perform rejection sampling to reject samples >= maxValue
            // 
            // However, such an approach requires a branch (for the loop) and therefore would generally be slower.
            return (int)(NextDoubleInner() * maxValue);
        }

        /// <summary>
        /// Generate a random Int32 over the interval [minValue, maxValue), i.e. excluding maxValue.
        /// maxValue must be >= minValue. minValue may be negative.
        /// </summary>
        public int Next(int minValue, int maxValue)
        {
            if (minValue > maxValue) {
                throw new ArgumentOutOfRangeException(nameof(maxValue), maxValue, "maxValue must be >= minValue");
            }

            long range = (long)maxValue - minValue;
            if (range <= int.MaxValue) {
                return (int)(NextDoubleInner() * range) + minValue;
            }
            // else

            // Notes. 
            // This xorshift PRNG generates 32 random bits per iteration. For double precision floats we use
            // all 32 of those bits, thus generating double values over a distribution of 2^32 possible values
            // in the interval [0, 1).
            // 
            // The maximum range in this method is UInt32.Max (==2^32), i.e. when minValue and maxValue are 
            // Int32.MinValue, Int32.MaxValue respectively. Therefore, when multiplying a random double by the
            // range, this method is capable of generating all integer values in the required range with uniform
            // distribution.
            //
            // In contrast, at time of writing System.Random generates doubles with only 2^31 possible values,
            // thus that class requires additional logic to compensate for that underlying problem.
            return (int)((long)(NextDoubleInner() * range) + minValue);
        }

        /// <summary>
        /// Generates a random double over the interval [0, 1), i.e. inclusive of 0.0 and exclusive of 1.0.
        /// </summary>
        public double NextDouble()
        {   
            return NextDoubleInner();
        }

        /// <summary>
        /// Fills the provided byte array with random bytes.
        /// </summary>
        /// <param name="buffer">The byte array to fill with random values.</param>
        public unsafe void NextBytes(byte[] buffer)
        {
            // For improved performance the below loop operates on these stack allocated copies of the heap variables.
            // Notes. doing this means that these heavily used variables are located near to other local/stack variables,
            // thus they will very likely be cached in the same CPU cache line.
            uint x=_x, y=_y, z=_z, w=_w;

            uint t;
            int i=0;

            // Get a pointer to the start of {buffer}; to do this we must pin {buffer} because it is allocated
            // on the heap and therefore could be moved by the GC at any time (if we didn't pin it).
            fixed(byte* pBuffer = buffer)
            {
                // A pointer to 32 bit size segments of {buffer}.
                uint* pUInt = (uint*)pBuffer;

                // Create and store new random bytes in groups of four.
                for(int bound = buffer.Length / 4; i < bound; i++)
                {
                    // Generate 32 random bits and assign to the segment that pUInt is currently pointing to.
                    t = x ^ (x << 11);

                    x = y;
                    y = z;
                    z = w;

                    pUInt[i] = w = (w^(w>>19))^(t^(t>>8));
                }
            }

            // Fill any trailing entries in {buffer} that occur when the its length is not a multiple of four.
            // Note. We do this using safe C# therefore can unpin {buffer}; i.e. its preferable to hold pins for the 
            // shortest duration possible because they have an impact on the effectiveness of the garbage collector.

            // Convert back to one based indexing instead of groups of four bytes.
            i = i * 4;

            // Fill any remaining bytes in the buffer.
            if(i < buffer.Length)
            {
                // Generate a further 32 random bits, and update PRNG state.
                t = x ^ (x << 11);

                x = y;
                y = z;
                z = w;

                w = (w^(w>>19))^(t^(t>>8));

                // Allocate one byte at a time until we reach the end of the buffer.
                while(i < buffer.Length)
                {
                    buffer[i++] = (byte)w;
                    w >>= 8;
                }              
            }

            // Update the state variables on the heap.
            _x = x;
            _y = y;
            _z = z;
            _w = w;
        }

        #endregion

        #region Public Methods [Methods not present on System.Random]

        /// <summary>
        /// Generate a random float over the interval [0, 1), i.e. inclusive of 0.0 and exclusive of 1.0.
        /// </summary>
        public float NextFloat()
        {
            // Note. Here we generate a random integer between 0 and 2^24-1 (i.e. 24 binary 1s) and multiply
            // by the fractional unit value 1.0 / 2^24, thus the result has a max value of
            // 1.0 - (1.0 / 2^24). Or 0.99999994 in decimal.
            return (NextInner() >> 8) * INCR_FLOAT;
        }

        /// <summary>
        /// Generate a random UInt32 over the interval [0, 2^32-1], i.e. over the full range of a UInt32.
        /// </summary>
        public uint NextUInt()
        {
            return NextInner();
        }

        /// <summary>
        /// Generate a random Int32 over interval [0 to Int32.MaxValue], i.e. inclusive of Int32.MaxValue.
        /// </summary>
        /// <remarks>
        /// This method can generate Int32.MaxValue, whereas Next() does not; this is the only difference
        /// between these two methods. As a consequence this method will typically be slightly faster because 
        /// Next () must test for Int32.MaxValue and resample the underlying RNG when that value occurs.
        /// </remarks>
        public int NextInt()
        {
            // Generate 32 random bits and shift right to leave the most significant 31 bits.
            // Bit 32 is the sign bit so must be zero to avoid negative results.
            // Note. Shift right is used instead of a mask because the high significant bits 
            // exhibit higher quality randomness compared to the lower bits.
            return (int)(NextInner() >> 1);
        }

        /// <summary>
        /// Generate a random UInt64 over the interval [0, 2^64-1], i.e. over the full range of a UInt64.
        /// </summary>
        public ulong NextULong()
        {
            return NextULongInner();
        }

        /// <summary>
        /// Generate a random double over the interval (0, 1), i.e. exclusive of both 0.0 and 1.0
        /// </summary>
        public double NextDoubleNonZero()
        {
            // Here we generate a random value in the interval [0, 0xffff_fffe], and add one
            // to generate a random value in the interval [1, 0xffff_ffff].
            //
            // We then multiply by the fractional unit 1.0 / 2^32 to obtain a floating point value 
            // in the interval [ 1/(2^32-1) , 1.0].
            return ((NextInner() & 0xffff_fffe) + 1) * INCR_DOUBLE;
        }

        /// <summary>
        /// Generate a single random bit.
        /// </summary>
        public bool NextBool()
        {
            // Generate 32 random bits and return the most significant bit, discarding the rest.
            // This is slower than the approach of generating and caching 32 bits for future calls, but 
            // (A) gives good quality randomness, and (B) is still very fast.
            return (NextInner() & 0x8000) == 0;
        }

        /// <summary>
        /// Generate a single random byte over the interval [0,255].
        /// </summary>
        public byte NextByte()
        {
            // Note. Here we shift right to use the 8 most significant bits because these exhibit higher quality
            // randomness than the lower bits.
            return (byte)(NextULongInner() >> 24);
        }

        #endregion

        #region Private Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double NextDoubleInner()
        {
            // Notes. 
            // Here we generate a random integer in the interval [0, 2^53-1]  (i.e. the max value is 53 binary 1s),
            // and multiply by the fractional value 1.0 / 2^53, thus the result has a min value of 0.0 and a max value of 
            // 1.0 - (1.0 / 2^53), or 0.99999999999999989 in decimal.
            //
            // I.e. we break the interval [0,1) into 2^53 uniformly distributed discrete values, and thus the interval between
            // two adjacent values is 1.0 / 2^53. This increment is chosen because it is the smallest value at which each 
            // distinct value in the full range (from 0.0 to 1.0 exclusive) can be represented directly by a double precision
            // float, and thus no rounding occurs in the representation of these values, which in turn ensures no bias in the 
            // random samples.
            // 
            // Note however that the total number of distinct values that can be represented by a double in the interval 
            // [0,1] is a little under 2^62, i.e. considerably more than the 2^53 values in the above described scheme,
            // e.g. that scheme will not generate any of the possible values in the interval (0, 2^-53). However, selecting 
            // from the full set of possible values uniformly will produce a highly biased distribution. 
            //
            // An alternative scheme exists that can produce all 2^62 (or so) values, and that produces a uniform distribution
            // over [0,1]; for an explanation see:
            //
            //    2014, Taylor R Campbell
            //
            //    Uniform random floats:  How to generate a double-precision
            //    floating-point number in [0, 1] uniformly at random given a uniform
            //    random source of bits.
            //
            //    https://mumble.net/~campbell/tmp/random_real.c
            //
            // That scheme is not employed here because its additional complexity will have significantly slower performance
            // compared to the simple shift and multiply performed here, and for most general purpose uses the 1/2^53 
            // resolution is more than sufficient, representing precision to approximately the 16th decimal place.                       
            return NextInner() * INCR_DOUBLE;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint NextInner()
        {
            // Generate 32 bits.
            uint t = _x ^ (_x << 11);

            _x = _y;
            _y = _z;
            _z = _w;

            return _w = (_w^(_w>>19)) ^ (t^(t>>8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong NextULongInner()
        {
            // Generate 32 bits.
            uint t = _x ^ (_x << 11);
            _x = _y;
            _y = _z;
            _z = _w;
            ulong acc = _w = (_w^(_w>>19)) ^ (t^(t>>8));

            // Generate a further 32 bits.
            t = _x ^ (_x << 11);
            _x = _y;
            _y = _z;
            _z = _w;
            return acc + (((ulong)(_w = (_w^(_w>>19)) ^ (t^(t>>8)))) << 32);
        }

        #endregion
    }
}
