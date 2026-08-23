function phase6Arithmetic(a, b)
{
    var total = a + b;
    return total * 2;
}

function phase6Loop(limit)
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

function phase6Classify(value)
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

function phase6Finally(value)
{
    var result = value;
    try
    {
        result += 2;
    }
    finally
    {
        result += 10;
    }
    return result;
}

function phase6Completion(value)
{
    var result = 0;
    try
    {
        if (value < 0)
            throw value;
        result = value;
        return result;
    }
    catch (error)
    {
        result = -error;
        return result;
    }
    finally
    {
        result += 10;
    }
}

function phase6ComputedUpdate(index)
{
    var values = [1, 2];
    var selected = index++;
    values[selected] += index;
    return index * 10 + values[0];
}

function phase6FinallyReturn(value)
{
    try
    {
        return value;
    }
    finally
    {
        return value + 10;
    }
}

function phase6FinallyThrow(value)
{
    try
    {
        try
        {
            return value;
        }
        finally
        {
            throw value + 2;
        }
    }
    catch (error)
    {
        return error + 1;
    }
}

function phase6NestedFinally(value)
{
    var result = value;
    try
    {
        try
        {
            result += 1;
            return result;
        }
        finally
        {
            result += 2;
        }
    }
    finally
    {
        result += 4;
    }
}

function phase6LoopFinally(limit)
{
    var sum = 0;
    for (var i = 0; i < limit; i++)
    {
        try
        {
            if (i == 1)
                continue;
            if (i == 3)
                break;
            sum += i;
        }
        finally
        {
            sum += 10;
        }
    }
    return sum;
}

function phase6Rethrow(value)
{
    try
    {
        try
        {
            throw value;
        }
        catch (error)
        {
            throw error + 1;
        }
    }
    catch (outer)
    {
        return outer + 2;
    }
}

function phase6NestedLoops(limit)
{
    var result = 0;
    for (var i = 0; i < limit; i++)
    {
        var skip = false;
        for (var j = 0; j < 4; j++)
        {
            if (j == 1)
            {
                skip = true;
                break;
            }
            result += i * 10 + j;
        }
        if (skip)
            continue;
        result += 1000;
    }
    return result;
}

function phase6SwitchLoop(limit)
{
    var result = 0;
    for (var i = 0; i < limit; i++)
    {
        switch (i)
        {
            case 1:
                continue;
            case 3:
                break;
            default:
                result += i;
        }
        result += 10;
    }
    return result;
}

function phase6CatchLoop(limit)
{
    var result = 0;
    for (var i = 0; i < limit; i++)
    {
        try
        {
            if (i == 1 || i == 3)
                throw i;
            result += i;
        }
        catch (error)
        {
            if (error == 1)
                continue;
            break;
        }
        result += 10;
    }
    return result;
}

function phase6NestedLoopExit(limit)
{
    var result = 0;
    for (var i = 0; i < limit; i++)
    {
        try
        {
            try
            {
                if (i == 1)
                    continue;
                if (i == 3)
                    break;
                result += i;
            }
            finally
            {
                result += 2;
            }
        }
        finally
        {
            result += 4;
        }
    }
    return result;
}

function phase6FinallyOverridesContinue(limit)
{
    var result = 0;
    for (var i = 0; i < limit; i++)
    {
        try
        {
            if (i == 1)
                continue;
            result += i;
        }
        finally
        {
            if (i == 1)
                break;
            result += 10;
        }
    }
    return result;
}

function phase6Coercions(value)
{
    var loose = value == "3";
    var strict = value === "3";
    return String(loose) + ":" + String(strict) + ":" +
        (Number("4.5") + 1);
}

function phase6CallOrder()
{
    var order = "";
    function mark(value)
    {
        order += value;
        return value;
    }
    function combine(a, b, c)
    {
        return a + b + c;
    }
    var combined = combine(mark("a"), mark("b"), mark("c"));
    return order + ":" + combined;
}

function phase6LiteralOrder()
{
    var order = "";
    function mark(value)
    {
        order += value;
        return value;
    }
    var values = [mark("a"), mark("b")];
    var record = { first: mark("c"), second: mark("d") };
    return order + ":" + values[0] + values[1] +
        record.first + record.second;
}

function phase6ComputedLValueOrder()
{
    var order = "";
    var holder = { value: 1 };
    function getHolder()
    {
        order += "r";
        return holder;
    }
    function getKey()
    {
        order += "k";
        return "value";
    }
    getHolder()[getKey()] += 2;
    return order + ":" + holder.value;
}

function phase6AccessorSemantics()
{
    var order = "";
    var holder = { stored: 1 };
    function readValue()
    {
        order += "g";
        return this.stored;
    }
    function writeValue(value)
    {
        order += "s";
        this.stored = value;
    }
    holder.addProperty("value", readValue, writeValue);
    holder.value += 2;
    return order + ":" + holder.stored;
}

function phase6PrototypeDispatch()
{
    function Box(value)
    {
        this.value = value;
    }
    Box.prototype.double = function()
    {
        return this.value * 2;
    };
    var box = new Box(4);
    return box.double();
}

function phase6UnaryBits(value)
{
    var mixed = ((value << 2) | 1) ^ (value & 2);
    var unsigned = -1 >>> 30;
    return String(~value) + ":" + mixed + ":" + unsigned + ":" +
        (17 % 5);
}

function phase6DeleteTypeof()
{
    var holder = { value: 7 };
    var before = typeof holder.value;
    var removed = delete holder.value;
    var after = typeof holder.value;
    return before + ":" + String(removed) + ":" + after;
}

function phase6ArgumentsAlias(a, b)
{
    arguments[0] += 1;
    return arguments.length * 100 + a * 10 + arguments[1];
}

function phase6MethodReceiver()
{
    var holder = { base: 4 };
    holder.add = function(value)
    {
        return this.base + value;
    };
    var detached = holder.add;
    return holder.add(3) + ":" + detached.call({ base: 10 }, 2);
}

function phase6ConstructorOrder()
{
    var order = "";
    function mark(value)
    {
        order += value;
        return value;
    }
    function Pair(first, second)
    {
        this.value = first + second;
    }
    var pair = new Pair(mark("a"), mark("b"));
    return order + ":" + pair.value;
}

function phase6InstanceOf()
{
    function Box(value)
    {
        this.value = value;
    }
    var box = new Box(5);
    var values = [];
    return String(box instanceof Box) + ":" +
        String(values instanceof Array) + ":" + typeof Box;
}

trace("PHASE6A:phase6Arithmetic=" + phase6Arithmetic(3, 4));
trace("PHASE6A:phase6Loop=" + phase6Loop(5));
trace("PHASE6A:phase6Classify=" + phase6Classify(2));
trace("PHASE6A:phase6Finally=" + phase6Finally(3));
trace("PHASE6A:phase6Completion=" + phase6Completion(-3));
trace("PHASE6A:phase6ComputedUpdate=" + phase6ComputedUpdate(0));
trace("PHASE6A:phase6FinallyReturn=" + phase6FinallyReturn(3));
trace("PHASE6A:phase6FinallyThrow=" + phase6FinallyThrow(3));
trace("PHASE6A:phase6NestedFinally=" + phase6NestedFinally(3));
trace("PHASE6A:phase6LoopFinally=" + phase6LoopFinally(5));
trace("PHASE6A:phase6Rethrow=" + phase6Rethrow(3));
trace("PHASE6A:phase6NestedLoops=" + phase6NestedLoops(3));
trace("PHASE6A:phase6SwitchLoop=" + phase6SwitchLoop(5));
trace("PHASE6A:phase6CatchLoop=" + phase6CatchLoop(5));
trace("PHASE6A:phase6NestedLoopExit=" + phase6NestedLoopExit(5));
trace("PHASE6A:phase6FinallyOverridesContinue=" + phase6FinallyOverridesContinue(5));
trace("PHASE6A:phase6Coercions=" + phase6Coercions(3));
trace("PHASE6A:phase6CallOrder=" + phase6CallOrder());
trace("PHASE6A:phase6LiteralOrder=" + phase6LiteralOrder());
trace("PHASE6A:phase6ComputedLValueOrder=" + phase6ComputedLValueOrder());
trace("PHASE6A:phase6AccessorSemantics=" + phase6AccessorSemantics());
trace("PHASE6A:phase6PrototypeDispatch=" + phase6PrototypeDispatch());
trace("PHASE6A:phase6UnaryBits=" + phase6UnaryBits(3));
trace("PHASE6A:phase6DeleteTypeof=" + phase6DeleteTypeof());
trace("PHASE6A:phase6ArgumentsAlias=" + phase6ArgumentsAlias(3, 4));
trace("PHASE6A:phase6MethodReceiver=" + phase6MethodReceiver());
trace("PHASE6A:phase6ConstructorOrder=" + phase6ConstructorOrder());
trace("PHASE6A:phase6InstanceOf=" + phase6InstanceOf());
fscommand("quit");
