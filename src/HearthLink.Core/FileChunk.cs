namespace HearthLink.Core;
public sealed record FileChunk(Guid Id,long Offset,byte[] Bytes);
