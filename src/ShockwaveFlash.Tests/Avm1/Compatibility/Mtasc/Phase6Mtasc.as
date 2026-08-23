class Phase6Mtasc
{
    public static function phase6gArithmetic(a, b)
    {
        var total = a + b;
        return total * 2;
    }

    public static function phase6gLoop(limit)
    {
        var sum = 0;
        for (var i = 0, c = 0; i < limit; i++, c += 2)
        {
            if (i == 2)
                continue;
            sum += i + c;
        }
        return sum;
    }

    public static function phase6gClassify(value)
    {
        var result;
        switch (value)
        {
            case 0:
                result = "zero";
                break;
            case 1:
            case 2:
                result = "small";
                break;
            default:
                result = "other";
        }
        return result;
    }

    public static function phase6gComputedUpdate(index)
    {
        var values = [1, 2];
        var selected = index++;
        values[selected] += index;
        return index * 10 + values[0];
    }

    public static function phase6gLiterals(value)
    {
        var values = [value, value + 1];
        var record = { left: values[0], right: values[1] };
        return record.left * 10 + record.right;
    }

    public static function phase6gShortCircuit(value)
    {
        var result = 0;
        if (value > 0 && (result = value + 1) > 0)
            result += 10;
        return result;
    }

    public static function phase6gClosure(value)
    {
        var add = function(delta)
        {
            return value + delta;
        };
        return add(4);
    }

    public static function main()
    {
        trace("PHASE6G:phase6gArithmetic=" + phase6gArithmetic(3, 4));
        trace("PHASE6G:phase6gLoop=" + phase6gLoop(5));
        trace("PHASE6G:phase6gClassify=" + phase6gClassify(2));
        trace("PHASE6G:phase6gComputedUpdate=" + phase6gComputedUpdate(0));
        trace("PHASE6G:phase6gLiterals=" + phase6gLiterals(3));
        trace("PHASE6G:phase6gShortCircuit=" + phase6gShortCircuit(3));
        trace("PHASE6G:phase6gClosure=" + phase6gClosure(3));
    }
}
