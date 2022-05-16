import './src/ArithmeticTests.fs.dart' as arithmetic;
import './src/ArrayTests.fs.dart' as array;
import './src/ComparisonTests.fs.dart' as comparison;
import './src/CustomOperatorTests.fs.dart' as custom_operator;
import './src/DateTimeTests.fs.dart' as date;
import './src/DictionaryTests.fs.dart' as dictionary;
import './src/EnumTests.fs.dart' as enum_;
import './src/EnumerableTests.fs.dart' as enumerable;
import './src/HashSetTests.fs.dart' as hash_set;
import './src/ListTests.fs.dart' as list;
import './src/MapTests.fs.dart' as map;
import './src/OptionTests.fs.dart' as option;
import './src/RecordTests.fs.dart' as record;
import './src/RegexTests.fs.dart' as regex;
import './src/ResizeArrayTests.fs.dart' as resize_array;
import './src/ResultTests.fs.dart' as result;
import './src/SeqTests.fs.dart' as seq;
import './src/SeqExpressionTests.fs.dart' as seq_expression;
import './src/SetTests.fs.dart' as set_;
import './src/StringTests.fs.dart' as string;
import './src/SudokuTests.fs.dart' as sudoku;
import './src/TailCallTests.fs.dart' as tailcall;
import './src/TimeSpanTests.fs.dart' as timespan;
import './src/TupleTests.fs.dart' as tuple;
import './src/UnionTests.fs.dart' as union;

void main() {
  arithmetic.tests();
  array.tests();
  comparison.tests();
  custom_operator.tests();
  date.tests();
  dictionary.tests();
  enum_.tests();
  enumerable.tests();
  hash_set.tests();
  list.tests();
  map.tests();
  option.tests();
  record.tests();
  regex.tests();
  resize_array.tests();
  result.tests();
  seq.tests();
  seq_expression.tests();
  set_.tests();
  string.tests();
  sudoku.tests();
  tailcall.tests();
  timespan.tests();
  tuple.tests();
  union.tests();
}