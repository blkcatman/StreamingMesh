using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;

public class TilePacker {
    int tx, ty, tz;
    List<ByteCoord> coords;

    public TilePacker(int tileX, int tileY, int tileZ) {
        tx = tileX;
        ty = tileY;
        tz = tileZ;
        coords = new List<ByteCoord>();
    }

    public VertexPack Pack() {
        VertexPack v = new VertexPack();

        v.tx = (byte)tx;
        v.ty = (byte)ty;
        v.tz = (byte)tz;

        v.poly1 = (byte)((coords.Count & 0xFF));
        v.poly2 = (byte)((coords.Count & 0xFF00) >> 8);
        v.poly3 = (byte)((coords.Count & 0xFF0000) >> 16);

        v.coords =  coords.ToArray();

        return v;
    }

	public byte[] PackToByteArray(int packSize) {
		byte[] packedBytes = new byte[6 + coords.Count * 5];
		packedBytes[0] = (byte)tx;
		packedBytes[1] = (byte)ty;
		packedBytes[2] = (byte)tz;

		packedBytes[3] = (byte)((coords.Count & 0xFF));
		packedBytes[4] = (byte)((coords.Count & 0xFF00) >> 8);
		packedBytes[5] = (byte)((coords.Count & 0xFF0000) >> 16);

		for(int i = 0; i < coords.Count; i ++) {
			int byteAlign = 6 + i * 5;
			packedBytes[byteAlign    ] = coords[i].p1;
			packedBytes[byteAlign + 1] = coords[i].p2;
			packedBytes[byteAlign + 2] = coords[i].p3;

            int compress = 0;
            compress += coords[i].x & 0x1F;
            compress += (coords[i].y & 0x1F) << 5;
            compress += (coords[i].z & 0x1F) << 10;
            packedBytes[byteAlign + 3] = (byte)(compress & 0xFF);
            packedBytes[byteAlign + 4] = (byte)((compress >> 8) & 0xFF);
		}

		return packedBytes;
	}

	public int[] getIndices() {
		int[] indices = new int[coords.Count];
		for(int i = 0; i < coords.Count; i++) {
			indices[i] = coords[i].p1;
			indices[i] += coords[i].p2 * 256;
			indices[i] += coords[i].p3 * 65536;
		}
		return indices;
	}

	public void AddVertex(ByteCoord vert) {
        coords.Add(vert);
    }

    public int GetByteSize() {
        //return 6 + coords.Count * 6;
        return 6 + coords.Count * 5;
    }
}

public struct ByteCoord {
    public byte p1;
    public byte p2;
    public byte p3;
    public byte x;
    public byte y;
    public byte z;
}

public struct VertexPack {
    public byte tx;
    public byte ty;
    public byte tz;
    public byte poly1;
    public byte poly2;
    public byte poly3;
    public ByteCoord[] coords;
}