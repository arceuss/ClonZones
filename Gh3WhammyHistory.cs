using System;
using Il2Cpp;
using UnityEngine;

namespace ClonZones
{
    internal sealed class Gh3WhammyHistory
    {
        private ObjectPublicTeNa1ByByUnique _owner;
        private Texture2D _texture;
        private IntPtr _buffer;

        public unsafe void Refresh()
        {
            var owner = ObjectPublicAbstractSealedInBoInObDoInSiDoInDoUnique.field_Private_Static_ObjectPublicTeNa1ByByUnique_0;
            if (owner == null) { _buffer = IntPtr.Zero; return; }
            Texture2D texture = owner.field_Private_Texture2D_0;
            if (_owner == owner && _texture == texture) return;
            _owner = owner;
            _texture = texture;
            var data = owner.field_Private_NativeArray_1_Byte_0;
            if (texture == null || texture.width != 1024 || texture.height != 32 || data.Length != 32768)
                throw new InvalidOperationException("CH whammy history has an unsupported layout.");
            _buffer = (IntPtr)data.m_Buffer;
        }

        public float Width(float worldZ, Vector4 properties)
        {
            if (_buffer == IntPtr.Zero) return 1f;
            int channel = (int)MathF.Floor(properties.x);
            if ((uint)channel >= 32) throw new InvalidOperationException("Invalid CH whammy channel.");
            float u = properties.y - worldZ / 55f;
            // CloneHero/SustainGlow's seven bilinear taps, read from its existing
            // 1024x32 R8 ring buffer instead of copying the texture off the GPU.
            float value = Sample(channel,u + .001708984375f) * .000388f
                + Sample(channel,u + .0009765625f) * .013304f
                + Sample(channel,u + .000244140625f) * .110982f
                + Sample(channel,u - .00048828125f) * .225084f
                + Sample(channel,u - .001220703125f) * .110982f
                + Sample(channel,u - .001953125f) * .013304f
                + Sample(channel,u - .002685546875f) * .000388f;
            float gain = value < .5f ? .5f * MathF.Sqrt(2f * value) : 1f - .5f * MathF.Sqrt(2f * (1f-value));
            float z = Math.Clamp(worldZ * 10f,0f,1f);
            float envelope = .4f + .6f * z*z*(3f-2f*z);
            // CH's zero-alpha boundary expands from .5 to .5+.5*gain*envelope.
            // Apply that width change while retaining GH3's cross-strip profile.
            return 1f + gain * envelope;
        }

        private unsafe float Sample(int channel,float u)
        {
            float x = u*1024f-.5f;
            int first = (int)MathF.Floor(x);
            float fraction = x-first;
            byte* row = (byte*)_buffer + channel*1024;
            return (row[first & 1023] + (row[(first+1) & 1023]-row[first & 1023])*fraction)/255f;
        }
    }
}
