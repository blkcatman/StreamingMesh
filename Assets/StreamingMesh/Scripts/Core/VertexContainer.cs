using UnityEngine;
using System;
using System.Linq;
using System.Collections.Generic;

namespace StreamingMesh.Core
{
  public class VertexContainer
  {
    List<int> m_PackedIndex = new List<int>();
    int m_Error = 0;

    int m_ContainerSize = 4;

    int m_PackageSize = 128;

    public VertexContainer(int packageSize, int containerSize)
    {
      m_PackageSize = packageSize;
      m_ContainerSize = containerSize;
    }

    public int Decode(
      byte[] source, ref float[][] dest,
      out float destX, out float destY, out float destZ,
      out int keyFrame
    )
    {
      keyFrame = 0;
      byte frame = source[0];
      int packages = ((int)source[7] << 16) + ((int)source[6] << 8) + source[5];

      destX = BitConverter.ToSingle(source, 9);
      destY = BitConverter.ToSingle(source, 13);
      destZ = BitConverter.ToSingle(source, 17);

      int offset = 21;

      if(frame == 0x0F)
      {
        keyFrame = 1;
        int indicesCnt = 0;
        m_PackedIndex.Clear();
        
        int hk = m_PackageSize / 2;
        float qk = (float)m_ContainerSize / (float)hk;
        float sqk = qk / 32.0f;

        for(int i = 0; i < packages; i++)
        {
          float t_x = (source[offset + 0] - hk) * qk; 
          float t_y = (source[offset + 1] - hk) * qk; 
          float t_z = (source[offset + 2] - hk) * qk; 
        
          int vertCount = 0;
          vertCount += source[offset + 3]; 
          vertCount += source[offset + 4] << 8;
          vertCount += source[offset + 5] << 16; 
        
          offset += 6;
        
          for(int j = 0; j < vertCount*5; j+=5)
          {   
            int vIdx = source[offset + j] + (source[offset + j + 1] << 8); 
            int mIdx = source[offset + j + 2]; 
            if (mIdx >= dest.Length || vIdx >= dest[mIdx].Length / 3) {
              Debug.LogError("data broken in VertexPack::Decode"); 
              m_Error = -1;
              return m_Error;
            }
        
            int compress = (int)source[offset + j + 3] + ((int)source[offset + j + 4] << 8);

            dest[mIdx][vIdx * 3    ] = t_x + ( compress        & 0x1F) * sqk;
            dest[mIdx][vIdx * 3 + 1] = t_y + ((compress >>  5) & 0x1F) * sqk;
            dest[mIdx][vIdx * 3 + 2] = t_z + ((compress >> 10) & 0x1F) * sqk;
        
            m_PackedIndex.Add((mIdx << 16) + vIdx);
            ++indicesCnt;
          }
          offset += (vertCount * 5); 
        }
        m_Error = 0;
      }
      else if (frame == 0x0E && m_Error == 0)
      {
        //keyFrame = 0;
        const float dd = 0.00006103515625f; // 1 / 16384;
        for (int i = 0; i < m_PackedIndex.Count; i++)
        {
          int idx  = m_PackedIndex[i];
          int mIdx = (idx >> 16) & 0xFF;
          int vIdx = idx & 0xFFFF;
          if(mIdx >= dest.Length || vIdx >= dest[mIdx].Length / 3) {
            Debug.LogError("data broken in VertexPack::Decode"); 
            m_Error = -1;
            return m_Error;
          }

          int dx = source[i * 3 + offset + 0] - 128;
          int dy = source[i * 3 + offset + 1] - 128;
          int dz = source[i * 3 + offset + 2] - 128;

          float fx = dest[mIdx][vIdx * 3    ] + (dx < 0 ? -(dx * dx) * dd : (dx * dx) * dd);
          float fy = dest[mIdx][vIdx * 3 + 1] + (dy < 0 ? -(dy * dy) * dd : (dy * dy) * dd);
          float fz = dest[mIdx][vIdx * 3 + 2] + (dz < 0 ? -(dz * dz) * dd : (dz * dz) * dd);

          dest[mIdx][vIdx * 3    ] = fx;
          dest[mIdx][vIdx * 3 + 1] = fy;
          dest[mIdx][vIdx * 3 + 2] = fz;
        }
        m_Error = 0;
      }

      return 0;
    }
  }
}