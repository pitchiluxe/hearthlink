namespace HearthLink.Core;
public sealed record VideoFrame(byte[] Jpeg,int Width,int Height,int Monitor,long Sequence,int CaptureMicroseconds=0,int EncodeMicroseconds=0,int RoundTripMilliseconds=0);
