using System.Text.RegularExpressions;

namespace ParserCombinators;

public static class Patterns {
	static readonly Dictionary<object, Pattern> PatternCache = new();

	static Pattern Cache(object key, Pattern sub) =>
		PatternCache.ContainsKey(key)
			? PatternCache[key]
			: PatternCache[key] = sub;

	public static Pattern Memoize(Pattern sub) =>
		text =>
			text.Memoization.ContainsKey((sub, text.Start))
				? text.Memoization[(sub, text.Start)]
				: text.Memoization[(sub, text.Start)] = sub(text);

	static readonly (Bobbin, dynamic)? None = null;

	public static readonly Pattern End =
		text => text.Length == 0 ? (text, null) : None;

	public static Pattern PositiveLookahead(Pattern sub) =>
		text => sub(text) != null ? (text, null) : None;

	public static Pattern NegativeLookahead(Pattern sub) =>
		text => sub(text) == null ? (text, null) : None;

	public static Pattern IgnoreLeadingWhitespace(Pattern sub) =>
		text => {
			var i = text.Start;
			for(; i < text.End; ++i)
				if(!char.IsWhiteSpace(text.String[i]))
					break;
			return sub(text.Forward(i - text.Start));
		};

	public static Pattern Sequence(params Pattern[] elems) =>
		text => {
			var list = new List<dynamic>();
			foreach(var elem in elems) {
				var match = elem(text);
				if(match == null) return null;
				text = match.Value.Item1;
				list.Add(match.Value.Item2);
			}
			return (text, list);
		};

	public static Pattern LooseSequence(params Pattern[] elems) =>
		Sequence(elems.Select(IgnoreLeadingWhitespace).ToArray());

	public static Pattern TupleSequence(Type[] types, params Pattern[] elems) {
		var tupleType = types.Length switch {
			2 => typeof(ValueTuple<,>), 3 => typeof(ValueTuple<,,>), 4 => typeof(ValueTuple<,,,>),
			5 => typeof(ValueTuple<,,,,>), 6 => typeof(ValueTuple<,,,,,>),
			7 => typeof(ValueTuple<,,,,,,>), 8 => typeof(ValueTuple<,,,,,,,>),
			_ => throw new NotImplementedException()
		};

		return text => {
			var list = new List<dynamic>();
			foreach(var elem in elems) {
				var match = elem(text);
				if(match == null) return null;
				text = match.Value.Item1;
				list.Add(match.Value.Item2);
			}

			return (text,
				Activator.CreateInstance(tupleType.MakeGenericType(types), list.ToArray()));
		};
	}

	public static Pattern TupleLooseSequence(Type[] types, params Pattern[] elems) =>
		TupleSequence(types, elems.Select(IgnoreLeadingWhitespace).ToArray());

	public static Pattern SavePass(Pattern sub) =>
		text => {
			if(BindStack.Count == 0)
				return sub(text);
			var saved = BindStack.Pop();
			BindStack.Push(saved.Copy());
			var ret = sub(text);
			if(ret != null) return ret;
			BindStack.Pop();
			BindStack.Push(saved);
			return null;
		};

	public static Pattern Choice(params Pattern[] opt) => text =>
		opt.Select(x => SavePass(x)(text)).FirstOrDefault(x => x != null);

	public static Pattern Optional(Pattern sub) =>
		text => SavePass(sub)(text) ?? (text, null);

	public static Pattern ZeroOrMore(Pattern sub) =>
		text => {
			sub = SavePass(sub);
			var list = new List<dynamic>();
			while(text.Length != 0) {
				var match = sub(text);
				if(match == null) break;
				text = match.Value.Item1;
				list.Add(match.Value.Item2);
			}
			return (text, list);
		};

	public static Pattern ZeroOrMore<T>(Pattern sub) =>
		text => {
			sub = SavePass(sub);
			var list = new List<T>();
			while(text.Length != 0) {
				var match = sub(text);
				if(match == null) break;
				text = match.Value.Item1;
				list.Add(match.Value.Item2);
			}
			return (text, list);
		};

	public static Pattern OneOrMore(Pattern sub) =>
		text => {
			sub = SavePass(sub);
			var list = new List<dynamic>();
			while(text.Length != 0) {
				var match = sub(text);
				if(match == null) break;
				text = match.Value.Item1;
				list.Add(match.Value.Item2);
			}

			if(list.Count == 0) return null;
			return (text, list);
		};

	public static Pattern OneOrMore<T>(Pattern sub) =>
		text => {
			sub = SavePass(sub);
			var list = new List<T>();
			while(text.Length != 0) {
				var match = sub(text);
				if(match == null) break;
				text = match.Value.Item1;
				list.Add(match.Value.Item2);
			}

			if(list.Count == 0) return null;
			return (text, list);
		};

	public static Pattern Literal(string val) =>
		Cache(val,
			text => text.ToString().StartsWith(val) ? (text.Forward(val.Length), val) : None);

	public static Pattern Regex(Regex regex) =>
		Cache(regex, text => {
			var match = regex.Match(text);
			return match.Success ? (text.Forward(match.Length), match.Value) : None;
		});

	public static Pattern Regex(string regex) => Regex(new Regex(regex));

	public class NamedAst {
		public readonly string Name;
		public dynamic Value;
		public readonly Dictionary<string, dynamic> Elements = new();

		public NamedAst(string name) =>
			Name = name;
	}

	public static Pattern NamedPattern(string name, Pattern sub) =>
		text => {
			var cur = new NamedAst(name);
			BindStack.Push(cur);
			var ret = sub(text);
			cur = (NamedAst) BindStack.Pop();
			if(ret == null) return null;
			cur.Value = ret.Value.Item2;
			return (ret.Value.Item1, cur);
		};

	public static Pattern Named(string name, Pattern sub) =>
		text => {
			var ret = sub(text);
			var cur = (NamedAst) BindStack.Peek();
			if(ret == null) {
				cur.Elements[name] = null;
				return null;
			}

			cur.Elements[name] = ret.Value.Item2;
			return ret;
		};

	public class ForwardPattern {
		public Pattern Value;
	}

	public static (Pattern, ForwardPattern) Forward() {
		var holder = new ForwardPattern();
		return (text => holder.Value(text), holder);
	}

	static readonly Stack<object> BindStack = new();

	public static Pattern Bind<T>(Pattern sub) where T : new() =>
		text => {
			var obj = new T();
			BindStack.Push(obj);
			var ret = sub(text);
			obj = (T) BindStack.Pop();
			if(ret == null) return null;
			return (ret.Value.Item1, obj);
		};

	public static Pattern With<T>(Action<T, dynamic> setter, Pattern sub) =>
		text => {
			var ret = sub(text);
			if(ret == null) return null;
			setter((T) BindStack.Peek(), ret.Value.Item2);
			return ret;
		};

	public static Pattern PushValue(Pattern sub) =>
		text => {
			BindStack.Pop(); // Pop off old or null value
			var ret = sub(text);
			BindStack.Push(ret?.Item2);
			return ret;
		};

	public static Pattern PopValue(Pattern sub) =>
		text => {
			BindStack.Push(null);
			var ret = sub(text);
			var value = BindStack.Pop();
			if(ret == null) return null;
			return (ret.Value.Item1, value);
		};

	public static Pattern ValueList(Pattern sub) =>
		text => {
			var value = new List<dynamic>();
			BindStack.Push(value);
			var ret = sub(text);
			value = (List<dynamic>) BindStack.Pop();
			if(ret == null) return null;
			return (ret.Value.Item1, value);
		};

	public static Pattern AddValue(Pattern sub) =>
		text => {
			var ret = sub(text);
			if(ret == null) return null;
			((List<dynamic>) BindStack.Peek()).Add(ret.Value.Item2);
			return ret;
		};
}