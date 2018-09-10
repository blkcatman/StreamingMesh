//SerializableDictionary.cs
//
//Redistributed from http://qiita.com/k_yanase/items/fb64ccfe1c14567a907d
//Original Editor: k_yanase http://qiita.com/k_yanase
//

using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Serialize {

	/// <summary>
	/// テーブルの管理クラス
	/// </summary>
	[System.Serializable]
	public class TableBase<TKey, TValue, Type> where Type : KeyAndValue<TKey, TValue> {
		[SerializeField]
		private List<Type> list;
		private Dictionary<TKey, TValue> table;


		public Dictionary<TKey, TValue> GetTable() {
			if(table == null) {
				table = ConvertListToDictionary(list);
			}
			return table;
		}

		/// <summary>
		/// Editor Only
		/// </summary>
		public List<Type> GetList() {
			return list;
		}

		static Dictionary<TKey, TValue> ConvertListToDictionary(List<Type> list) {
			Dictionary<TKey, TValue> dic = new Dictionary<TKey, TValue>();
			foreach(KeyAndValue<TKey, TValue> pair in list) {
				dic.Add(pair.MaterialName, pair.Shader);
			}
			return dic;
		}
	}

	/// <summary>
	/// シリアル化できる、KeyValuePair
	/// </summary>
	[System.Serializable]
	public class KeyAndValue<TKey, TValue> {
		public TKey MaterialName;
		public TValue Shader;

		public KeyAndValue(TKey key, TValue value) {
			MaterialName = key;
			Shader = value;
		}
		public KeyAndValue(KeyValuePair<TKey, TValue> pair) {
			MaterialName = pair.Key;
			Shader = pair.Value;
		}


	}
}