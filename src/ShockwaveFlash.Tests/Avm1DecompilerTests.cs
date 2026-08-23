using System.Text.RegularExpressions;
using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ast;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Text;
using ShockwaveFlash.Avm1.Types;
using ShockwaveFlash.Tags;
using ShockwaveFlash.Tags.Action;
using ShockwaveFlash.Tags.Control;
using ShockwaveFlash.Tags.Metadata;
using ShockwaveFlash.Tags.Sprite;
using ShockwaveFlash.Types;
using ShockwaveFlash.Types.Control;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1DecompilerTests
{
    private static int Len(Avm1Action action) => Avm1Action.EncodeCollection([action], swfVersion: 6).Length;

    private static int[] ComputeOffsets(IReadOnlyList<Avm1Action> actions)
    {
        var offsets = new int[actions.Count];
        var cursor = 0;

        for (var i = 0; i < actions.Count; i++)
        {
            offsets[i] = cursor;
            cursor += Len(actions[i]);
        }

        return offsets;
    }

    private static void SetBranch(List<Avm1Action> actions, int index, int targetOffset)
    {
        var offsets = ComputeOffsets(actions);
        var length = Len(actions[index]);
        var branch = (short)(targetOffset - (offsets[index] + length));

        actions[index] = actions[index] switch
        {
            ActionIf => new ActionIf(branch),
            ActionJump => new ActionJump(branch),
            _ => actions[index]
        };
    }

    private static List<Avm1Action> BuildThreeCaseChain(bool strictEquality)
    {
        Avm1Action Equality() => strictEquality ? new ActionStrictEquals() : new ActionEquals2();

        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("before")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("kind")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("a")]),
            Equality(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.String("kind")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("b")]),
            Equality(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.String("kind")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("c")]),
            Equality(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 8, offsets[13]);
        SetBranch(actions, index: 12, offsets[36]);
        SetBranch(actions, index: 18, offsets[23]);
        SetBranch(actions, index: 22, offsets[36]);
        SetBranch(actions, index: 28, offsets[33]);
        SetBranch(actions, index: 32, offsets[36]);
        return actions;
    }

    private static List<Avm1Action> BuildThreeCaseMemberChain()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("before")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionSetVariable()
        };
        var ifIndices = new List<int>();
        var jumpIndices = new List<int>();
        var caseStarts = new List<int>();
        var caseNames = new[] { "a", "b", "c" };

        for (var i = 0; i < caseNames.Length; i++)
        {
            caseStarts.Add(actions.Count);
            actions.AddRange([
                new ActionPush([PushValue.Register(1)]),
                new ActionPush([PushValue.String("_kind")]),
                new ActionGetMember(),
                new ActionPush([PushValue.String(caseNames[i])]),
                new ActionEquals2(),
                new ActionNot()
            ]);
            ifIndices.Add(actions.Count);
            actions.Add(new ActionIf(0));
            actions.AddRange([
                new ActionPush([PushValue.String("y")]),
                new ActionPush([PushValue.Integer(i + 1)]),
                new ActionSetVariable()
            ]);
            jumpIndices.Add(actions.Count);
            actions.Add(new ActionJump(0));
        }

        var defaultStart = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionSetVariable()
        ]);
        var mergeStart = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetVariable()
        ]);

        var offsets = ComputeOffsets(actions);
        for (var i = 0; i < ifIndices.Count; i++)
        {
            var next = i + 1 < caseStarts.Count ? caseStarts[i + 1] : defaultStart;
            SetBranch(actions, ifIndices[i], offsets[next]);
            SetBranch(actions, jumpIndices[i], offsets[mergeStart]);
        }

        return actions;
    }

    private readonly record struct SwitchBodySpec(
        IReadOnlyList<Avm1Action> Actions,
        bool JumpsToMerge);

    private static List<Avm1Action> BuildDirectThreeCaseSwitch(
        IReadOnlyList<int> bodyByCase,
        IReadOnlyList<SwitchBodySpec> bodies,
        SwitchBodySpec defaultBody,
        bool includeContinuation = true)
    {
        var actions = new List<Avm1Action>();
        var ifIndices = new int[3];
        var caseNames = new[] { "a", "b", "c" };
        for (var i = 0; i < caseNames.Length; i++)
        {
            actions.Add(new ActionPush([PushValue.String("kind")]));
            actions.Add(new ActionGetVariable());
            actions.Add(new ActionPush([PushValue.String(caseNames[i])]));
            actions.Add(new ActionStrictEquals());
            ifIndices[i] = actions.Count;
            actions.Add(new ActionIf(0));
        }

        var jumpIndices = new List<int>();
        actions.AddRange(defaultBody.Actions);
        if (defaultBody.JumpsToMerge)
        {
            jumpIndices.Add(actions.Count);
            actions.Add(new ActionJump(0));
        }

        var bodyStarts = new int[bodies.Count];
        for (var i = 0; i < bodies.Count; i++)
        {
            bodyStarts[i] = actions.Count;
            actions.AddRange(bodies[i].Actions);
            if (bodies[i].JumpsToMerge)
            {
                jumpIndices.Add(actions.Count);
                actions.Add(new ActionJump(0));
            }
        }

        var mergeStart = actions.Count;
        if (includeContinuation)
        {
            actions.AddRange([
                new ActionPush([PushValue.String("z")]),
                new ActionPush([PushValue.Integer(5)]),
                new ActionSetVariable()
            ]);
        }

        var offsets = ComputeOffsets(actions);
        var mergeOffset = includeContinuation
            ? offsets[mergeStart]
            : offsets[^1] + Len(actions[^1]);
        for (var i = 0; i < ifIndices.Length; i++)
            SetBranch(actions, ifIndices[i], offsets[bodyStarts[bodyByCase[i]]]);
        foreach (var jumpIndex in jumpIndices)
            SetBranch(actions, jumpIndex, mergeOffset);

        return actions;
    }

    private static List<Avm1Action> BuildGroupedTwoCaseSwitchWithDispatchRegister(
        bool readRegisterAfterSwitch)
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("kind")]),
            new ActionGetVariable(),
            new ActionStoreRegister(5),
            new ActionPush([PushValue.String("CASE_A")]),
            new ActionGetVariable(),
            new ActionStrictEquals(),
            new ActionIf(0),

            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.String("CASE_B")]),
            new ActionGetVariable(),
            new ActionStrictEquals(),
            new ActionIf(0),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(12)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionSetVariable(),

            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetVariable()
        };
        if (readRegisterAfterSwitch)
        {
            actions.AddRange([
                new ActionPush([PushValue.String("after")]),
                new ActionPush([PushValue.Register(5)]),
                new ActionSetVariable()
            ]);
        }

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 6, offsets[13]);
        SetBranch(actions, index: 11, offsets[13]);
        SetBranch(actions, index: 12, offsets[17]);
        SetBranch(actions, index: 16, offsets[20]);
        return actions;
    }

    private static List<Avm1Action> BuildGroupedTwoCaseSwitchWithSemanticAndScratchRegisters()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("kind")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),

            new ActionPush([PushValue.Register(2)]),
            new ActionStoreRegister(0),
            new ActionPush([PushValue.String("CASE_A")]),
            new ActionGetVariable(),
            new ActionStrictEquals(),
            new ActionIf(0),

            new ActionPush([PushValue.Register(0)]),
            new ActionPush([PushValue.String("CASE_B")]),
            new ActionGetVariable(),
            new ActionStrictEquals(),
            new ActionIf(0),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(12)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionSetVariable(),

            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 9, offsets[16]);
        SetBranch(actions, index: 14, offsets[16]);
        SetBranch(actions, index: 15, offsets[20]);
        SetBranch(actions, index: 19, offsets[23]);
        return actions;
    }

    private static List<Avm1Action> BuildTerminalGroupedDefaultSwitchWithDispatchRegister()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("kind")]),
            new ActionGetVariable(),
            new ActionStoreRegister(5),
            new ActionPush([PushValue.String("a")]),
            new ActionStrictEquals(),
            new ActionIf(0),

            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.String("b")]),
            new ActionStrictEquals(),
            new ActionIf(0),

            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.String("c")]),
            new ActionStrictEquals(),
            new ActionIf(0),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var endOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 5, offsets[15]);
        SetBranch(actions, index: 9, offsets[19]);
        SetBranch(actions, index: 13, offsets[23]);
        SetBranch(actions, index: 14, offsets[23]);
        SetBranch(actions, index: 18, endOffset);
        SetBranch(actions, index: 22, endOffset);
        SetBranch(actions, index: 26, endOffset);
        return actions;
    }

    private static List<Avm1Action> BuildDistinctTwoCaseSwitchWithDispatchRegister()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("kind")]),
            new ActionGetVariable(),
            new ActionStoreRegister(5),
            new ActionPush([PushValue.String("a")]),
            new ActionStrictEquals(),
            new ActionIf(0),

            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.String("b")]),
            new ActionStrictEquals(),
            new ActionIf(0),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var endOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 5, offsets[11]);
        SetBranch(actions, index: 9, offsets[15]);
        SetBranch(actions, index: 10, endOffset);
        SetBranch(actions, index: 14, endOffset);
        SetBranch(actions, index: 18, endOffset);
        return actions;
    }

    private static List<Avm1Action> BuildTwoCaseSwitchWithGuardedTerminalDefault()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("kind")]),
            new ActionGetVariable(),
            new ActionStoreRegister(5),
            new ActionPush([PushValue.String("a")]),
            new ActionStrictEquals(),
            new ActionIf(0),

            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.String("b")]),
            new ActionStrictEquals(),
            new ActionIf(0),

            new ActionPush([PushValue.String("skip")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionJump(0),
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var endOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 5, offsets[19]);
        SetBranch(actions, index: 9, offsets[23]);
        SetBranch(actions, index: 13, offsets[15]);
        SetBranch(actions, index: 14, endOffset);
        SetBranch(actions, index: 18, endOffset);
        SetBranch(actions, index: 22, endOffset);
        SetBranch(actions, index: 26, endOffset);
        return actions;
    }

    private static List<Avm1Action> BuildConditionalBreakFallThroughSwitch(bool breakOnTrue)
    {
        var actions = new List<Avm1Action>();
        var caseIfIndices = new int[3];
        var caseNames = new[] { "a", "b", "c" };
        for (var i = 0; i < caseNames.Length; i++)
        {
            actions.Add(new ActionPush([PushValue.String("kind")]));
            actions.Add(new ActionGetVariable());
            actions.Add(new ActionPush([PushValue.String(caseNames[i])]));
            actions.Add(new ActionStrictEquals());
            caseIfIndices[i] = actions.Count;
            actions.Add(new ActionIf(0));
        }

        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionSetVariable()
        ]);
        var defaultJump = actions.Count;
        actions.Add(new ActionJump(0));

        var bodyStarts = new int[3];
        bodyStarts[0] = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable()
        ]);
        var conditionalJump = actions.Count;
        actions.Add(new ActionIf(0));

        var breakTrampoline = -1;
        if (!breakOnTrue)
        {
            breakTrampoline = actions.Count;
            actions.Add(new ActionJump(0));
        }

        var fallThroughContinuation = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        ]);

        bodyStarts[1] = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        ]);
        var secondCaseJump = actions.Count;
        actions.Add(new ActionJump(0));

        bodyStarts[2] = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        ]);
        var thirdCaseJump = actions.Count;
        actions.Add(new ActionJump(0));

        var mergeStart = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetVariable()
        ]);

        var offsets = ComputeOffsets(actions);
        for (var i = 0; i < caseIfIndices.Length; i++)
            SetBranch(actions, caseIfIndices[i], offsets[bodyStarts[i]]);

        SetBranch(actions, defaultJump, offsets[mergeStart]);
        SetBranch(actions, secondCaseJump, offsets[mergeStart]);
        SetBranch(actions, thirdCaseJump, offsets[mergeStart]);
        if (breakOnTrue)
        {
            SetBranch(actions, conditionalJump, offsets[mergeStart]);
        }
        else
        {
            SetBranch(actions, conditionalJump, offsets[fallThroughContinuation]);
            SetBranch(actions, breakTrampoline, offsets[mergeStart]);
        }

        return actions;
    }

    private static List<Avm1Action> BuildMixedTerminalExitSwitch(
        bool conditionalBreak,
        bool fallsThrough,
        bool throws)
    {
        var actions = new List<Avm1Action>();
        var caseIfIndices = new int[3];
        var caseNames = new[] { "a", "b", "c" };
        for (var i = 0; i < caseNames.Length; i++)
        {
            actions.Add(new ActionPush([PushValue.String("kind")]));
            actions.Add(new ActionGetVariable());
            actions.Add(new ActionPush([PushValue.String(caseNames[i])]));
            actions.Add(new ActionStrictEquals());
            caseIfIndices[i] = actions.Count;
            actions.Add(new ActionIf(0));
        }

        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionSetVariable()
        ]);
        var defaultJump = actions.Count;
        actions.Add(new ActionJump(0));

        var bodyStarts = new int[3];
        bodyStarts[0] = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("terminal")]),
            new ActionGetVariable()
        ]);
        var terminalJump = actions.Count;
        actions.Add(new ActionIf(0));

        var conditionalBreakJump = -1;
        if (conditionalBreak)
        {
            actions.AddRange([
                new ActionPush([PushValue.String("stop")]),
                new ActionGetVariable()
            ]);
            conditionalBreakJump = actions.Count;
            actions.Add(new ActionIf(0));
        }

        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        ]);
        var firstCaseJump = -1;
        if (!fallsThrough)
        {
            firstCaseJump = actions.Count;
            actions.Add(new ActionJump(0));
        }

        bodyStarts[1] = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        ]);
        var secondCaseJump = actions.Count;
        actions.Add(new ActionJump(0));

        bodyStarts[2] = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        ]);
        var thirdCaseJump = actions.Count;
        actions.Add(new ActionJump(0));

        var terminalStart = actions.Count;
        if (throws)
        {
            actions.AddRange([
                new ActionPush([PushValue.String("bad")]),
                new ActionThrow()
            ]);
        }
        else
        {
            actions.AddRange([
                new ActionPush([PushValue.Integer(1)]),
                new ActionReturn()
            ]);
        }

        var mergeStart = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetVariable()
        ]);

        var offsets = ComputeOffsets(actions);
        for (var i = 0; i < caseIfIndices.Length; i++)
            SetBranch(actions, caseIfIndices[i], offsets[bodyStarts[i]]);

        SetBranch(actions, defaultJump, offsets[mergeStart]);
        SetBranch(actions, terminalJump, offsets[terminalStart]);
        SetBranch(actions, secondCaseJump, offsets[mergeStart]);
        SetBranch(actions, thirdCaseJump, offsets[mergeStart]);
        if (conditionalBreak)
            SetBranch(actions, conditionalBreakJump, offsets[mergeStart]);
        if (!fallsThrough)
            SetBranch(actions, firstCaseJump, offsets[mergeStart]);

        return actions;
    }

    private static List<Avm1Action> BuildVisibleBreakArmFallThroughSwitch(bool breakOnTrue)
    {
        var actions = new List<Avm1Action>();
        var caseIfIndices = new int[3];
        var caseNames = new[] { "a", "b", "c" };
        for (var i = 0; i < caseNames.Length; i++)
        {
            actions.Add(new ActionPush([PushValue.String("kind")]));
            actions.Add(new ActionGetVariable());
            actions.Add(new ActionPush([PushValue.String(caseNames[i])]));
            actions.Add(new ActionStrictEquals());
            caseIfIndices[i] = actions.Count;
            actions.Add(new ActionIf(0));
        }

        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionSetVariable()
        ]);
        var defaultJump = actions.Count;
        actions.Add(new ActionJump(0));

        var bodyStarts = new int[3];
        bodyStarts[0] = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable()
        ]);
        if (!breakOnTrue)
            actions.Add(new ActionNot());
        var conditionalJump = actions.Count;
        actions.Add(new ActionIf(0));
        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        ]);

        bodyStarts[1] = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        ]);
        var secondCaseJump = actions.Count;
        actions.Add(new ActionJump(0));

        bodyStarts[2] = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        ]);
        var thirdCaseJump = actions.Count;
        actions.Add(new ActionJump(0));

        var breakArmStart = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(9)]),
            new ActionSetVariable()
        ]);
        var breakArmJump = actions.Count;
        actions.Add(new ActionJump(0));

        var mergeStart = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetVariable()
        ]);

        var offsets = ComputeOffsets(actions);
        for (var i = 0; i < caseIfIndices.Length; i++)
            SetBranch(actions, caseIfIndices[i], offsets[bodyStarts[i]]);
        SetBranch(actions, defaultJump, offsets[mergeStart]);
        SetBranch(actions, conditionalJump, offsets[breakArmStart]);
        SetBranch(actions, secondCaseJump, offsets[mergeStart]);
        SetBranch(actions, thirdCaseJump, offsets[mergeStart]);
        SetBranch(actions, breakArmJump, offsets[mergeStart]);

        return actions;
    }

    private static List<Avm1Action> BuildSwitchInsideWhile(bool includeOuterLoopExits = false)
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("loop")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0)
        };

        var caseIfIndices = new int[3];
        var caseNames = new[] { "a", "b", "c" };
        for (var i = 0; i < caseNames.Length; i++)
        {
            actions.Add(new ActionPush([PushValue.String("kind")]));
            actions.Add(new ActionGetVariable());
            actions.Add(new ActionPush([PushValue.String(caseNames[i])]));
            actions.Add(new ActionStrictEquals());
            caseIfIndices[i] = actions.Count;
            actions.Add(new ActionIf(0));
        }

        actions.AddRange([
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionSetVariable()
        ]);
        var defaultJump = actions.Count;
        actions.Add(new ActionJump(0));

        var bodyStarts = new int[3];
        var caseJumps = new int[3];
        var outerLoopControlIfs = new[] { -1, -1 };
        for (var i = 0; i < bodyStarts.Length; i++)
        {
            bodyStarts[i] = actions.Count;
            if (includeOuterLoopExits && i < outerLoopControlIfs.Length)
            {
                actions.AddRange([
                    new ActionPush([PushValue.String(i == 0 ? "skip" : "done")]),
                    new ActionGetVariable()
                ]);
                outerLoopControlIfs[i] = actions.Count;
                actions.Add(new ActionIf(0));
            }

            actions.AddRange([
                new ActionPush([PushValue.String("x")]),
                new ActionPush([PushValue.Integer(i + 1)]),
                new ActionSetVariable()
            ]);
            caseJumps[i] = actions.Count;
            actions.Add(new ActionJump(0));
        }

        var switchMerge = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetVariable()
        ]);
        var loopJump = actions.Count;
        actions.Add(new ActionJump(0));

        var loopExit = actions.Count;
        actions.AddRange([
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(6)]),
            new ActionSetVariable()
        ]);

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[loopExit]);
        for (var i = 0; i < caseIfIndices.Length; i++)
        {
            SetBranch(actions, caseIfIndices[i], offsets[bodyStarts[i]]);
            SetBranch(actions, caseJumps[i], offsets[switchMerge]);
        }
        SetBranch(actions, defaultJump, offsets[switchMerge]);
        SetBranch(actions, loopJump, offsets[0]);
        if (includeOuterLoopExits)
        {
            SetBranch(actions, outerLoopControlIfs[0], offsets[0]);
            SetBranch(actions, outerLoopControlIfs[1], offsets[loopExit]);
        }

        return actions;
    }

    [Fact]
    public void Stack_value_retains_positional_record_update_surface()
    {
        var original = new Avm1StackValue(
            new ValueIndex(1),
            Avm1StackValueKind.Constant,
            "1",
            1);

        var updated = original with
        {
            Index = new ValueIndex(2),
            Kind = Avm1StackValueKind.Temporary,
            Display = "updated",
            IntValue = 2
        };

        updated.Index.ShouldBe(new ValueIndex(2));
        updated.Kind.ShouldBe(Avm1StackValueKind.Temporary);
        updated.Display.ShouldBe("updated");
        updated.IntValue.ShouldBe(2);
    }

    [Theory]
    [InlineData("__Packages.CraftingMenu", "CraftingMenu", "CraftingMenu")]
    [InlineData("__Packages.gfx.controls.Button", "gfx.controls.Button", "Button")]
    [InlineData("Dofus::Sound::Manager", "Dofus.Sound.Manager", "Manager")]
    [InlineData("CraftingMenu", "CraftingMenu", "CraftingMenu")]
    public void Class_decompiler_normalizes_avm1_package_names(
        string encodedName,
        string expectedClassName,
        string expectedConstructorName)
    {
        var className = Avm1ClassDecompiler.NormalizeClassName(encodedName);

        className.ShouldBe(expectedClassName);
        Avm1ClassDecompiler.GetConstructorName(className).ShouldBe(expectedConstructorName);
    }

    [Fact]
    public void Class_decompiler_builds_portable_paths_for_legacy_qualified_names()
    {
        Avm1ClassDecompiler.GetClassOutputRelativePath("Dofus::Sound::Manager")
            .ShouldBe(Path.Combine("Dofus", "Sound", "Manager.as"));
        Avm1ClassDecompiler.GetClassOutputRelativePath("pkg.CON")
            .ShouldBe(Path.Combine("pkg", "_CON.as"));
        Avm1ClassDecompiler.GetClassOutputRelativePath("pkg.Bad?Name")
            .ShouldBe(Path.Combine("pkg", "Bad_Name.as"));
    }

    [Fact]
    public void Class_decompiler_recovers_legacy_register_class_constructor_alias()
    {
        const byte swfVersion = 6;
        const string runtimeClassName = "Legacy::Widgets::Widget";
        const string constructorAlias = "Class_Legacy_Widgets_Widget";
        var constructorBody = Avm1Action.EncodeCollection(
        [
            new ActionPush([PushValue.String("this")]),
            new ActionGetVariable(),
            new ActionPush([
                PushValue.String("ready"),
                PushValue.Boolean(true)]),
            new ActionSetMember()
        ], swfVersion);
        var methodBody = Avm1Action.EncodeCollection(
        [
            new ActionPush([PushValue.Integer(7)]),
            new ActionReturn()
        ], swfVersion);
        var initializer = Avm1Action.EncodeCollection(
        [
            new ActionPush([PushValue.String(constructorAlias)]),
            new ActionDefineFunction(string.Empty, [], constructorBody),
            new ActionSetVariable(),
            new ActionPush([PushValue.String(constructorAlias)]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("prototype")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("measure")]),
            new ActionDefineFunction(string.Empty, [], methodBody),
            new ActionSetMember(),
            new ActionPush([PushValue.String(constructorAlias)]),
            new ActionGetVariable(),
            new ActionPush([
                PushValue.String(runtimeClassName),
                PushValue.Integer(2),
                PushValue.String("Object")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("registerClass")]),
            new ActionCallMethod(),
            new ActionPop(),
            new ActionEnd()
        ], swfVersion);
        var swf = new ShockwaveFlashFile(
            new ShockwaveFlashHeader(
                ShockwaveFlashCompression.None,
                swfVersion,
                FileLength: 0,
                FrameSize: new Rectangle(0, 0, 100, 100),
                FrameRate: Fixed8.Zero,
                FrameCount: 1),
        [
            new ExportAssetsTag(
                new TagMetadata(TagCode.ExportAssets, 0, 0),
                [new AssetReference(1, runtimeClassName)]),
            new DefineSpriteTag(
                new TagMetadata(TagCode.DefineSprite, 0, 0),
                id: 1,
                numFrames: 1,
                tags: [new EndTag(new TagMetadata(TagCode.End, 0, 0))]),
            new DoInitActionTag(
                new TagMetadata(TagCode.DoInitAction, 0, 0),
                id: 1,
                initializer),
            new EndTag(new TagMetadata(TagCode.End, 0, 0))
        ]);

        Avm1ClassDecompiler.TryDecompileSourceFile(
                swf,
                runtimeClassName,
                out var sourceFile)
            .ShouldBeTrue();
        var sourceClass = sourceFile.ShouldNotBeNull().Classes.ShouldHaveSingleItem();
        sourceClass.Name.Value.ShouldBe("Legacy.Widgets.Widget");
        sourceClass.Constructor.ShouldNotBeNull().Name.ShouldBe("Widget");
        sourceClass.Constructor.Body.GetAs2Text().ShouldContain("this.ready = true;");
        sourceClass.Methods.Select(method => method.Name).ShouldBe(["measure"]);
        sourceFile.GetAs2Text().ShouldNotContain(constructorAlias);
    }

    [Fact]
    public void Class_decompiler_does_not_project_as3_symbol_class_entries()
    {
        var swf = new ShockwaveFlashFile(
            new ShockwaveFlashHeader(
                ShockwaveFlashCompression.None,
                Version: 10,
                FileLength: 0,
                FrameSize: new Rectangle(0, 0, 100, 100),
                FrameRate: Fixed8.Zero,
                FrameCount: 1),
        [
            new SymbolClassTag(
                new TagMetadata(TagCode.SymbolClass, 0, 0),
                [new SymbolReference(1, "as3.widgets.Widget")]),
            new DefineSpriteTag(
                new TagMetadata(TagCode.DefineSprite, 0, 0),
                id: 1,
                numFrames: 1,
                tags: [new EndTag(new TagMetadata(TagCode.End, 0, 0))]),
            new EndTag(new TagMetadata(TagCode.End, 0, 0))
        ]);

        Avm1ClassDecompiler.DecompileSourceFiles(swf).ShouldBeEmpty();
        Avm1ClassDecompiler.TryDecompileSourceFile(
                swf,
                "as3.widgets.Widget",
                out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void Class_decompiler_does_not_project_exports_from_an_as3_swf()
    {
        var swf = new ShockwaveFlashFile(
            new ShockwaveFlashHeader(
                ShockwaveFlashCompression.None,
                Version: 40,
                FileLength: 0,
                FrameSize: new Rectangle(0, 0, 100, 100),
                FrameRate: Fixed8.Zero,
                FrameCount: 1),
        [
            new FileAttributesTag(
                new TagMetadata(TagCode.FileAttributes, 0, 0),
                FileAttributesFlags.IsActionScript3),
            new ExportAssetsTag(
                new TagMetadata(TagCode.ExportAssets, 0, 0),
                [new AssetReference(1, "assets.Widget")]),
            new DefineSpriteTag(
                new TagMetadata(TagCode.DefineSprite, 0, 0),
                id: 1,
                numFrames: 1,
                tags: [new EndTag(new TagMetadata(TagCode.End, 0, 0))]),
            new EndTag(new TagMetadata(TagCode.End, 0, 0))
        ]);

        Avm1ClassDecompiler.DecompileSourceFiles(swf).ShouldBeEmpty();
    }

    [Fact]
    public void Deeply_nested_if_regions_fall_back_to_opaque_source_without_stack_overflow()
    {
        const int nestingDepth = 160;
        var actions = new List<Avm1Action>();
        var branchIndices = new int[nestingDepth];
        for (var i = 0; i < nestingDepth; i++)
        {
            actions.Add(new ActionPush([PushValue.String("condition")]));
            actions.Add(new ActionGetVariable());
            actions.Add(new ActionNot());
            branchIndices[i] = actions.Count;
            actions.Add(new ActionIf(0));
        }

        actions.Add(new ActionPush([
            PushValue.String("result"),
            PushValue.Boolean(true)]));
        actions.Add(new ActionSetVariable());
        var mergeIndex = actions.Count;
        actions.Add(new ActionEnd());

        var offsets = ComputeOffsets(actions);
        foreach (var branchIndex in branchIndices)
            SetBranch(actions, branchIndex, offsets[mergeIndex]);

        var method = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var source = method.ProjectSource();

        method.StructuredAst.Nodes.ShouldContain(node =>
            node.Kind == Avm1AstNodeKind.Opaque);
        source.IsComplete.ShouldBeFalse();
        source.Arena.OpaqueRegions.ShouldNotBeEmpty();
    }

    [Fact]
    public void Structured_block_budget_preserves_an_oversized_method_as_opaque_source()
    {
        var actions = BuildThreeCaseChain(strictEquality: true);

        var method = Avm1Decompiler.DecompileMethod(
            actions,
            swfVersion: 6,
            options: new Avm1MethodDecompilationOptions
            {
                MaximumStructuredBlockCount = 2
            });
        var source = method.ProjectSource();

        method.ControlFlowGraph.Count.ShouldBeGreaterThan(2);
        method.StructuredAst.Nodes.Count(node =>
            node.Kind == Avm1AstNodeKind.Opaque).ShouldBe(1);
        source.IsComplete.ShouldBeFalse();
        source.Arena.OpaqueRegions.Count.ShouldBe(1);
    }

    [Fact]
    public void Conditional_loop_break_can_target_the_virtual_action_stream_exit()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("loop")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("stop")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionJump(0)
        };
        var offsets = ComputeOffsets(actions);
        var streamEnd = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, 3, streamEnd);
        SetBranch(actions, 6, streamEnd);
        SetBranch(actions, 7, offsets[0]);

        var method = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        method.StructuredAst.Nodes.ShouldContain(node =>
            node.Kind == Avm1AstNodeKind.Break);
        method.GetStructuredAs2Text().ShouldContain("break;");
    }

    [Fact]
    public void Class_decompiler_preserves_legacy_function_parameters()
    {
        var function = new ActionDefineFunction(
            string.Empty,
            ["a_layoutData", "a_viewData", "a_columnData", "a_defaultsData"],
            ReadOnlyMemory<byte>.Empty);

        var upgraded = Avm1ClassDecompiler.UpgradeFunction(function);

        upgraded.Parameters.Select(parameter => parameter.Name).ShouldBe(function.Parameters);
        upgraded.Parameters.Select(parameter => parameter.Register).ShouldAllBe(register => register == 0);
    }

    [Fact]
    public void Class_decompiler_uses_constant_pool_active_at_function_definition()
    {
        IReadOnlyList<Avm1Action> actions =
        [
            new ActionConstantPool(["oldName"]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionConstantPool(["activeName"]),
            new ActionDefineFunction(string.Empty, [], ReadOnlyMemory<byte>.Empty)
        ];

        Avm1ClassDecompiler.FindActiveConstantPool(actions, actionIndex: 1).ShouldBe(["oldName"]);
        Avm1ClassDecompiler.FindActiveConstantPool(actions, actionIndex: 3).ShouldBe(["activeName"]);
    }

    [Fact]
    public void Class_decompiler_separates_static_methods_from_nested_closure_fields()
    {
        var constructorBody = Avm1Action.EncodeCollection([
            new ActionPush([
                PushValue.Register(1),
                PushValue.String("value"),
                PushValue.Register(2)]),
            new ActionSetMember()
        ], swfVersion: 6);
        var closureBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn()
        ], swfVersion: 6);
        var staticMethodBody = Avm1Action.EncodeCollection([
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 0,
                flags: (FunctionFlags)0,
                parameters: [],
                body: closureBody),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([
                PushValue.Register(1),
                PushValue.String("target"),
                PushValue.Register(2)]),
            new ActionSetMember(),
            new ActionPush([PushValue.String("Demo")]),
            new ActionGetVariable(),
            new ActionPush([
                PushValue.String("_cache"),
                PushValue.Boolean(true)]),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(1)]),
            new ActionReturn()
        ], swfVersion: 6);
        var instanceMethodBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn()
        ], swfVersion: 6);
        var getterBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn()
        ], swfVersion: 6);
        var setterBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn()
        ], swfVersion: 6);
        var initializerActions = new List<Avm1Action>([
            new ActionPush([PushValue.String("Demo")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 2,
                flags: FunctionFlags.PreloadThis,
                parameters: [new FunctionParameter(2, "value")],
                body: constructorBody),
            new ActionStoreRegister(1),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(1), PushValue.String("prototype")]),
            new ActionGetMember(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([
                PushValue.Register(2),
                PushValue.String("enabled"),
                PushValue.Boolean(true)]),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(1), PushValue.String("create")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 3,
                flags: (FunctionFlags)0,
                parameters: [
                    new FunctionParameter(2, "target"),
                    new FunctionParameter(3, "callback")
                ],
                body: staticMethodBody),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(2), PushValue.String("createDelegate")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 2,
                flags: FunctionFlags.PreloadThis,
                parameters: [new FunctionParameter(2, "target")],
                body: instanceMethodBody),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(2), PushValue.String("__get__status")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 1,
                flags: FunctionFlags.PreloadThis,
                parameters: [],
                body: getterBody),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(2), PushValue.String("__set__status")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 2,
                flags: FunctionFlags.PreloadThis,
                parameters: [new FunctionParameter(2, "value")],
                body: setterBody),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(2), PushValue.String("__set__status")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Register(2), PushValue.String("__get__status")]),
            new ActionGetMember(),
            new ActionPush([
                PushValue.String("status"),
                PushValue.Integer(3),
                PushValue.Register(2),
                PushValue.String("addProperty")]),
            new ActionCallMethod(),
            new ActionPop(),
            new ActionPush([
                PushValue.Register(1),
                PushValue.String("_settings"),
                PushValue.String("enabled"),
                PushValue.Boolean(true),
                PushValue.Integer(1)]),
            new ActionInitObject(),
            new ActionSetMember(),
            new ActionPush([
                PushValue.Register(1),
                PushValue.String("_items"),
                PushValue.Integer(0)]),
            new ActionInitArray(),
            new ActionSetMember(),
            new ActionPush([
                PushValue.Register(1),
                PushValue.String("_initialized"),
                PushValue.Integer(0),
                PushValue.Register(1),
                PushValue.String("create")]),
            new ActionCallMethod(),
            new ActionSetMember(),
            new ActionPush([PushValue.String("skipInitializerSideEffect")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("initializer-side-effect")]),
            new ActionTrace()
        ]);
        var initializerEndOffset = initializerActions.Sum(Len);
        SetBranch(initializerActions, initializerActions.Count - 3, initializerEndOffset);
        var initializer = Avm1Action.EncodeCollection(initializerActions, swfVersion: 6);
        var swf = new ShockwaveFlashFile(
            new ShockwaveFlashHeader(
                Compression: ShockwaveFlashCompression.None,
                Version: 6,
                FileLength: 0,
                FrameSize: new Rectangle(0, 0, 0, 0),
                FrameRate: Fixed8.Zero,
                FrameCount: 1),
            [
                new ExportAssetsTag(
                    new TagMetadata(TagCode.ExportAssets, 0, 0),
                    [new AssetReference(1, "Demo")]),
                new DefineSpriteTag(
                    new TagMetadata(TagCode.DefineSprite, 0, 0),
                    id: 1,
                    numFrames: 1,
                    tags: []),
                new DoInitActionTag(
                    new TagMetadata(TagCode.DoInitAction, 0, 0),
                    id: 1,
                    data: initializer)
            ]);
        var output = Path.Combine(
            Path.GetTempPath(),
            "ShockwaveFlash.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Avm1ClassDecompiler.DecompileToFolder(swf, output);
            var text = File.ReadAllText(Path.Combine(output, "Demo.as"));

            text.ShouldContain("   var value;");
            text.ShouldContain("   var enabled = true;");
            text.ShouldContain("   static function create(target, callback)");
            text.ShouldContain("   function createDelegate(target)");
            text.ShouldContain("   static var _settings = {enabled: true};");
            text.ShouldContain("   static var _items = [];");
            text.Split('\n')
                .Single(line => line.Contains("static var _initialized", StringComparison.Ordinal))
                .TrimEnd('\r')
                .ShouldBe("   static var _initialized = Demo.create();");
            text.ShouldContain("   static var _cache;");
            text.ShouldNotContain("static var create");
            text.ShouldNotContain("static var target");
            text.ShouldNotContain(" = function;");

            var sourceFile = Avm1ClassDecompiler.DecompileSourceFiles(swf).Single(file =>
                file.Classes.Any(sourceClass => sourceClass.Name.Value == "Demo"));
            var sourceClass = sourceFile.Classes.Single();
            sourceFile.IsComplete.ShouldBeTrue();
            sourceClass.Name.Value.ShouldBe("Demo");
            sourceClass.BaseType.ShouldBeNull();
            sourceClass.Origin.SpriteId.ShouldBe((ushort)1);
            sourceClass.Fields.Single(field => field.Name == "enabled")
                .Initializer!.GetAs2Text().ShouldBe("true");
            sourceClass.Fields.Single(field => field.Name == "_settings")
                .Modifiers.HasFlag(Avm1SourceDeclarationModifiers.Static).ShouldBeTrue();
            sourceClass.Constructor.ShouldNotBeNull();
            sourceClass.Methods.Single(method => method.Name == "create")
                .Modifiers.HasFlag(Avm1SourceDeclarationModifiers.Static).ShouldBeTrue();
            var sourceInitializer = sourceClass.Initializer.ShouldNotBeNull();
            sourceInitializer.IsComplete.ShouldBeTrue();
            sourceInitializer.Body.GetAs2Text()
                .ShouldContain("trace(\"initializer-side-effect\");");
            sourceInitializer.Body.GetAs2Text().ShouldContain("skipInitializerSideEffect");
            var initializerRoot = sourceInitializer.Body.Arena[sourceInitializer.Body.Body];
            initializerRoot.Children.Count.ShouldBe(1);
            sourceInitializer.Body.Arena.Statements.Any(statement =>
                statement.Kind is Avm1SourceStatementKind.If).ShouldBeTrue();
            sourceInitializer.Steps.Any(step =>
                step.Kind is Avm1SourceClassInitializationStepKind.ClassDefinition)
                .ShouldBeTrue();
            sourceInitializer.Steps.Any(step =>
                step.Kind is Avm1SourceClassInitializationStepKind.ClassDefinition &&
                step.Member is Avm1SourceMethodDeclaration
                {
                    Kind: Avm1SourceMethodKind.Constructor
                }).ShouldBeTrue();
            sourceInitializer.Steps.Any(step =>
                step.Kind is Avm1SourceClassInitializationStepKind.MemberDefinition &&
                step.Member?.Name == "_initialized").ShouldBeTrue();
            var accessorStep = sourceInitializer.Steps.Single(step =>
                step.Kind is Avm1SourceClassInitializationStepKind.AccessorDefinition);
            accessorStep.Member.ShouldBeOfType<Avm1SourceMethodDeclaration>()
                .Name.ShouldBe("status");
            sourceInitializer.ResidualStatements.Count.ShouldBe(1);
            sourceInitializer.Steps[^1].Kind
                .ShouldBe(Avm1SourceClassInitializationStepKind.ResidualStatement);
            NormalizeNewLines(sourceFile.GetAs2Text()).ShouldBe(NormalizeNewLines(text));

            var sourceProgram = Avm1ClassDecompiler.DecompileSourceProgram(swf);
            sourceProgram.Files.Count.ShouldBe(1);
            NormalizeNewLines(sourceProgram.CreateProjection().Files.Single().GetAs2Text())
                .ShouldBe(NormalizeNewLines(text));
            sourceProgram.TryGetClassSymbol("Demo", out var demoSymbol).ShouldBeTrue();
            sourceProgram[demoSymbol].Kind.ShouldBe(Avm1SourceProgramSymbolKind.Class);
            sourceProgram.TryGetSymbol(
                sourceProgram.Files.Single().Classes.Single(),
                out var projectedDemoSymbol).ShouldBeTrue();
            projectedDemoSymbol.ShouldBe(demoSymbol);
            var programInitializer = sourceProgram.Files.Single().Classes.Single()
                .Initializer.ShouldNotBeNull();
            sourceProgram.TryGetBindings(
                programInitializer.Body.Arena,
                out var initializerBindings).ShouldBeTrue();
            initializerBindings!.ContainingMember.IsValid.ShouldBeFalse();
            initializerBindings.IsStaticContext.ShouldBeTrue();

            Avm1ClassDecompiler.TryDecompileSourceFile(
                swf,
                "Demo",
                out var selectedSourceFile).ShouldBeTrue();
            selectedSourceFile!.Classes.Single().Name.Value.ShouldBe("Demo");
            Avm1ClassDecompiler.TryDecompileSourceFile(
                swf,
                "Missing",
                out _).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    private static string NormalizeNewLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void Builds_instruction_table_with_offsets()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.Instructions.Count.ShouldBe(3);
        result.Instructions[new ActionIndex(0)].Offset.ShouldBe(0);
        result.Instructions[new ActionIndex(1)].Offset.ShouldBe(Len(actions[0]));
        result.Instructions.ByteLength.ShouldBe(actions.Sum(Len));
    }

    [Fact]
    public void Flow_graph_uses_packed_normal_only_fast_path_without_completion_edges()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionPop()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.ControlFlowGraph.CompletionEdges.ShouldBeEmpty();
        result.FlowGraph.Contexts.Count.ShouldBe(1);
        result.FlowGraph.Points.Count.ShouldBe(result.ControlFlowGraph.Count);
        result.FlowGraph.GetContext(result.FlowGraph.NormalContext).State.ShouldBe(Avm1FlowState.Normal);
        result.StackIr.Values.ShouldNotContain(value =>
            value.Kind == Avm1StackValueKind.Projection);
        result.TacIr.Instructions.ShouldNotContain(instruction =>
            (instruction.Flags & Avm1IrInstructionFlags.ContextProjection) != 0);
        foreach (var block in result.ControlFlowGraph.Blocks)
        {
            result.FlowGraph.TryGetPoint(
                block.Index,
                result.FlowGraph.NormalContext,
                out var point).ShouldBeTrue();
            result.FlowGraph[point].Block.ShouldBe(block.Index);
        }
    }

    [Fact]
    public void Instruction_table_expands_with_body_and_keeps_branch_regions_distinct()
    {
        var body = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };
        SetBranch(body, index: 2, body.Sum(Len));
        var withAction = new ActionWith(Avm1Action.EncodeCollection(body, swfVersion: 6));
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("scope")]),
            new ActionGetVariable(),
            withAction,
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        ];

        var table = Avm1InstructionTable.Build(actions, swfVersion: 6);

        table.Count.ShouldBe(actions.Length + body.Count + 1);
        var region = table.WithRegions.Single();
        table[region.EnterAction].Kind.ShouldBe(Avm1InstructionKind.WithEnter);
        table[region.EnterAction].Region.ShouldBe(CodeRegionIndex.Root);
        table[region.BodyStartAction].Region.ShouldBe(region.BodyRegion);
        table[region.ExitAction].Kind.ShouldBe(Avm1InstructionKind.WithExit);

        var nestedIf = new ActionIndex(region.BodyStartAction.Value + 2);
        table.GetBranchTargetAction(nestedIf).ShouldBe(region.ExitAction);
        var parentContinuation = new ActionIndex(region.ExitAction.Value + 1);
        table[region.ExitAction].Offset.ShouldBe(table[parentContinuation].Offset);
        table.TryGetActionAtOffset(table[parentContinuation].Offset, out var physicalTarget)
            .ShouldBeTrue();
        physicalTarget.ShouldBe(parentContinuation);
    }

    [Fact]
    public void Structured_as2_emitter_lowers_action_with_through_shared_ir()
    {
        var body = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("x")]),
                new ActionPush([PushValue.Integer(1)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("scope")]),
            new ActionGetVariable(),
            new ActionWith(body),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.Instructions.WithRegions.Count.ShouldBe(1);
        result.StackDepthAnalysis.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Message.Contains("underflow", StringComparison.OrdinalIgnoreCase));
        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.WithEnter);
        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.WithExit);
        result.TacIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1TacOp.WithEnter);
        result.TacIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1TacOp.WithExit);
        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.With)
            .ShouldBe(1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "with (scope)",
            "{",
            "    x = 1;",
            "}",
            "after = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Action_with_shares_register_ssa_with_parent_method()
    {
        var body = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionStoreRegister(5),
            new ActionPop()
        };
        SetBranch(body, index: 3, body.Sum(Len));
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(7)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("scope")]),
            new ActionGetVariable(),
            new ActionWith(Avm1Action.EncodeCollection(body, swfVersion: 6)),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.RegisterSsa.Accesses
            .Where(access => access.Register == 5)
            .Select(access => access.Version)
            .Distinct()
            .Count().ShouldBeGreaterThan(1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc5_ = 7;",
            "with (scope)",
            "{",
            "    if (flag)",
            "    {",
            "        _loc5_ += 1;",
            "    }",
            "}",
            "after = _loc5_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_nested_action_with_regions()
    {
        var innerBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("x")]),
                new ActionPush([PushValue.Integer(1)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        var outerBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("innerScope")]),
                new ActionGetVariable(),
                new ActionWith(innerBody),
                new ActionPush([PushValue.String("y")]),
                new ActionPush([PushValue.Integer(2)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("outerScope")]),
            new ActionGetVariable(),
            new ActionWith(outerBody),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.Instructions.WithRegions.Count.ShouldBe(2);
        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.With)
            .ShouldBe(2);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "with (outerScope)",
            "{",
            "    with (innerScope)",
            "    {",
            "        x = 1;",
            "    }",
            "    y = 2;",
            "}",
            "z = 3;",
            string.Empty
        ]));
    }

    [Fact]
    public void Instruction_table_expands_action_try_with_explicit_handler_edges()
    {
        var tryBody = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };
        SetBranch(tryBody, index: 2, tryBody.Sum(Len));
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("caught")]),
                new ActionPush([PushValue.Boolean(true)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        var finallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("cleaned")]),
                new ActionPush([PushValue.Boolean(true)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionTry(
                TryFlags.CatchBlock | TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: "error",
                Avm1Action.EncodeCollection(tryBody, swfVersion: 6),
                catchBody,
                finallyBody),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        ];

        var table = Avm1InstructionTable.Build(actions, swfVersion: 6);
        var region = table.TryRegions.Single();

        table[region.EnterAction].Kind.ShouldBe(Avm1InstructionKind.TryEnter);
        table[region.TryBodyStartAction].Region.ShouldBe(region.TryBodyRegion);
        table[region.TryExitAction].Kind.ShouldBe(Avm1InstructionKind.TryExit);
        table[region.CatchEnterAction].Kind.ShouldBe(Avm1InstructionKind.CatchEnter);
        table[region.CatchBodyStartAction].Region.ShouldBe(region.CatchBodyRegion);
        table[region.CatchExitAction].Kind.ShouldBe(Avm1InstructionKind.CatchExit);
        table[region.FinallyEnterAction].Kind.ShouldBe(Avm1InstructionKind.FinallyEnter);
        table[region.FinallyBodyStartAction].Region.ShouldBe(region.FinallyBodyRegion);
        table[region.FinallyExitAction].Kind.ShouldBe(Avm1InstructionKind.FinallyExit);

        var nestedIf = new ActionIndex(region.TryBodyStartAction.Value + 2);
        table.GetBranchTargetAction(nestedIf).ShouldBe(region.TryExitAction);
        table[region.FinallyExitAction].Offset.ShouldBe(table[region.ContinuationAction].Offset);
        table.TryGetActionAtOffset(table[region.ContinuationAction].Offset, out var physicalTarget)
            .ShouldBeTrue();
        physicalTarget.ShouldBe(region.ContinuationAction);

        var cfg = Avm1ControlFlowGraph.Build(table);
        cfg.TryGetBlockForAction(region.EnterAction, out var header).ShouldBeTrue();
        cfg.TryGetBlockForAction(region.TryBodyStartAction, out var tryEntry).ShouldBeTrue();
        cfg.TryGetBlockForAction(region.TryExitAction, out var tryExit).ShouldBeTrue();
        cfg.TryGetBlockForAction(region.CatchEnterAction, out var catchEnter).ShouldBeTrue();
        cfg.TryGetBlockForAction(region.CatchBodyStartAction, out var catchEntry).ShouldBeTrue();
        cfg.TryGetBlockForAction(region.CatchExitAction, out var catchExit).ShouldBeTrue();
        cfg.TryGetBlockForAction(region.FinallyEnterAction, out var finallyEnter).ShouldBeTrue();
        cfg.TryGetBlockForAction(region.FinallyExitAction, out var finallyExit).ShouldBeTrue();
        cfg.TryGetBlockForAction(region.ContinuationAction, out var continuation).ShouldBeTrue();
        var getVariableAction = new ActionIndex(region.TryBodyStartAction.Value + 1);
        var trySetVariableAction = new ActionIndex(region.TryBodyStartAction.Value + 5);
        var catchSetVariableAction = new ActionIndex(region.CatchBodyStartAction.Value + 2);
        cfg.TryGetBlockForAction(getVariableAction, out var getVariableBlock).ShouldBeTrue();
        cfg.TryGetBlockForAction(trySetVariableAction, out var trySetVariableBlock).ShouldBeTrue();
        cfg.TryGetBlockForAction(catchSetVariableAction, out var catchSetVariableBlock).ShouldBeTrue();

        cfg[header].Terminator.ShouldBe(Avm1BlockTerminatorKind.FallThrough);
        cfg[header].FirstSuccessor.ShouldBe(tryEntry);
        cfg[header].SecondSuccessor.ShouldBe(BlockIndex.Invalid);
        cfg[tryEntry].ExceptionSuccessor.ShouldBe(BlockIndex.Invalid);
        cfg[getVariableBlock].StartAction.ShouldBe(getVariableAction);
        cfg[getVariableBlock].EndAction.ShouldBe(new ActionIndex(getVariableAction.Value + 1));
        cfg[getVariableBlock].ExceptionSuccessor.ShouldBe(catchEnter);
        cfg[getVariableBlock].ExceptionStackSource.ShouldBe(header);
        cfg[trySetVariableBlock].ExceptionSuccessor.ShouldBe(catchEnter);
        cfg[trySetVariableBlock].ExceptionStackSource.ShouldBe(header);
        cfg[tryExit].FirstSuccessor.ShouldBe(finallyEnter);
        cfg[catchEnter].Terminator.ShouldBe(Avm1BlockTerminatorKind.FallThrough);
        cfg[catchEnter].FirstSuccessor.ShouldBe(catchEntry);
        cfg[catchEnter].SecondSuccessor.ShouldBe(BlockIndex.Invalid);
        cfg[catchEntry].ExceptionSuccessor.ShouldBe(BlockIndex.Invalid);
        cfg[catchSetVariableBlock].ExceptionSuccessor.ShouldBe(finallyEnter);
        cfg[catchSetVariableBlock].ExceptionStackSource.ShouldBe(header);
        cfg[catchExit].FirstSuccessor.ShouldBe(finallyEnter);
        cfg[finallyExit].FirstSuccessor.ShouldBe(continuation);
    }

    [Fact]
    public void Structured_as2_emitter_lowers_try_catch_finally_without_fallthrough()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("x")]),
                new ActionPush([PushValue.Integer(1)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("caught")]),
                new ActionPush([PushValue.String("error")]),
                new ActionGetVariable(),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        var finallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("cleanup")]),
                new ActionPush([PushValue.Integer(3)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionTry(
                TryFlags.CatchBlock | TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: "error",
                tryBody,
                catchBody,
                finallyBody),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackDepthAnalysis.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Message.Contains("underflow", StringComparison.OrdinalIgnoreCase));
        result.StackIr.Instructions.ShouldContain(instruction => instruction.Op == Avm1StackIrOp.TryEnter);
        result.StackIr.Instructions.ShouldContain(instruction => instruction.Op == Avm1StackIrOp.CatchEnter);
        result.StackIr.Instructions.ShouldContain(instruction => instruction.Op == Avm1StackIrOp.FinallyEnter);
        result.TacIr.Instructions.ShouldContain(instruction => instruction.Op == Avm1TacOp.TryExit);
        result.TacIr.Instructions.ShouldContain(instruction => instruction.Op == Avm1TacOp.CatchExit);
        result.TacIr.Instructions.ShouldContain(instruction => instruction.Op == Avm1TacOp.FinallyExit);
        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.Try).ShouldBe(1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "try",
            "{",
            "    x = 1;",
            "}",
            "catch (error)",
            "{",
            "    caught = error;",
            "}",
            "finally",
            "{",
            "    cleanup = 3;",
            "}",
            "after = 4;",
            string.Empty
        ]));
    }

    [Fact]
    public void Catch_in_register_defines_ssa_name_without_synthetic_assignment()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("x")]),
                new ActionPush([PushValue.Integer(1)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("caught")]),
                new ActionPush([PushValue.Register(7)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionTry(
                TryFlags.CatchBlock | TryFlags.CatchInRegister,
                catchRegister: 7,
                catchVariable: string.Empty,
                tryBody,
                catchBody,
                ReadOnlyMemory<byte>.Empty),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        var catchWrite = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 7 && access.Kind == Avm1RegisterAccessKind.Write);
        result.CatchPayloads.TryGetBinding(
            region.CatchEnterAction,
            out var binding).ShouldBeTrue();

        result.TacIr[catchWrite.Instruction].Op.ShouldBe(Avm1TacOp.CatchEnter);
        binding.Flags.ShouldBe(Avm1CatchPayloadFlags.HasUnknownSource);
        binding.SourceCount.ShouldBe(0);
        result.CatchPayloads.TryGetExactSource(
            region.CatchEnterAction,
            out _).ShouldBeFalse();
        catchWrite.Source.ShouldBe(ValueIndex.Invalid);
        result.RegisterSsa.Accesses.ShouldContain(access =>
            access.Register == 7 &&
            access.Kind == Avm1RegisterAccessKind.Read &&
            access.Version == catchWrite.Version);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "try",
            "{",
            "    x = 1;",
            "}",
            "catch (_loc7_)",
            "{",
            "    caught = _loc7_;",
            "}",
            "after = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Explicit_throw_payload_flows_into_register_catch_value_analysis()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("boom")]),
                new ActionThrow()
            ],
            swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("caught")]),
                new ActionPush([PushValue.Register(7)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionTry(
                TryFlags.CatchBlock | TryFlags.CatchInRegister,
                catchRegister: 7,
                catchVariable: string.Empty,
                tryBody,
                catchBody,
                ReadOnlyMemory<byte>.Empty)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        var throwInstruction = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.Throw);
        result.CatchPayloads.TryGetBinding(
            region.CatchEnterAction,
            out var binding).ShouldBeTrue();
        result.CatchPayloads.TryGetExactSource(
            region.CatchEnterAction,
            out var catchSource).ShouldBeTrue();
        var catchWrite = result.RegisterSsa.Accesses.Single(access =>
            access.Action == region.CatchEnterAction &&
            access.Register == 7 &&
            access.Kind == Avm1RegisterAccessKind.Write);
        var catchRead = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 7 &&
            access.Kind == Avm1RegisterAccessKind.Read);
        result.SymbolTable.TryGetRegisterSymbol(
            catchRead.Register,
            catchRead.Version,
            out var catchSymbol).ShouldBeTrue();

        binding.Flags.ShouldBe(Avm1CatchPayloadFlags.None);
        result.CatchPayloads.GetSources(binding).ToArray().ShouldBe([throwInstruction.Operand0]);
        catchSource.ShouldBe(throwInstruction.Operand0);
        catchWrite.Source.ShouldBe(throwInstruction.Operand0);
        result.ValueAnalysis[catchRead.Value].ConstantKind.ShouldBe(Avm1ConstantKind.String);
        result.ValueAnalysis[catchRead.Value].StringValue.ShouldBe("boom");
        result.ValueOrigins[catchRead.Value].ShouldBe(Avm1ValueOrigin.StringLiteral("boom"));
        catchSymbol.Type.ShouldBe(Avm1InferredType.String);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "try",
            "{",
            "    throw \"boom\";",
            "}",
            "catch (_loc7_)",
            "{",
            "    caught = \"boom\";",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Explicit_throw_payload_flows_into_variable_catch_value_analysis()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("boom")]),
                new ActionThrow()
            ],
            swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("caught")]),
                new ActionPush([PushValue.String("error")]),
                new ActionGetVariable(),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionTry(
                TryFlags.CatchBlock,
                catchRegister: 0,
                catchVariable: "error",
                tryBody,
                catchBody,
                ReadOnlyMemory<byte>.Empty)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        var throwInstruction = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.Throw);
        var catchRead = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.GetVariable &&
            instruction.Action.Value >= region.CatchBodyStartAction.Value &&
            instruction.Action.Value < region.CatchExitAction.Value);

        result.CatchPayloads.TryGetExactVariableSource(
            catchRead.Action,
            "error",
            out var catchSource).ShouldBeTrue();
        catchSource.ShouldBe(throwInstruction.Operand0);
        result.ValueAnalysis[catchRead.Result].ConstantKind.ShouldBe(Avm1ConstantKind.String);
        result.ValueAnalysis[catchRead.Result].StringValue.ShouldBe("boom");
        result.ValueOrigins[catchRead.Result].ShouldBe(Avm1ValueOrigin.StringLiteral("boom"));
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "try",
            "{",
            "    throw \"boom\";",
            "}",
            "catch (error)",
            "{",
            "    caught = \"boom\";",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Reassigned_variable_catch_does_not_reuse_original_throw_fact()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("boom")]),
                new ActionThrow()
            ],
            swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("error")]),
                new ActionPush([PushValue.String("changed")]),
                new ActionSetVariable(),
                new ActionPush([PushValue.String("caught")]),
                new ActionPush([PushValue.String("error")]),
                new ActionGetVariable(),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionTry(
                TryFlags.CatchBlock,
                catchRegister: 0,
                catchVariable: "error",
                tryBody,
                catchBody,
                ReadOnlyMemory<byte>.Empty)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        var catchRead = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.GetVariable &&
            instruction.Action.Value >= region.CatchBodyStartAction.Value &&
            instruction.Action.Value < region.CatchExitAction.Value);

        result.ValueAnalysis[catchRead.Result].ConstantKind.ShouldBe(Avm1ConstantKind.Unknown);
        result.ValueOrigins[catchRead.Result].ShouldBe(Avm1ValueOrigin.Variable("error"));
        result.GetStructuredAs2Text().ShouldContain("error = \"changed\";");
        result.GetStructuredAs2Text().ShouldContain("caught = error;");
        result.GetStructuredAs2Text().ShouldNotContain("caught = \"boom\";");
    }

    [Fact]
    public void Constructed_error_payload_names_and_types_register_catch()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionThrow()
            ],
            swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("caught")]),
                new ActionPush([PushValue.Register(7)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("Error")]),
            new ActionNewObject(),
            new ActionTry(
                TryFlags.CatchBlock | TryFlags.CatchInRegister,
                catchRegister: 7,
                catchVariable: string.Empty,
                tryBody,
                catchBody,
                ReadOnlyMemory<byte>.Empty)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        var constructed = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.NewObject);
        var catchWrite = result.RegisterSsa.Accesses.Single(access =>
            access.Action == region.CatchEnterAction &&
            access.Register == 7 &&
            access.Kind == Avm1RegisterAccessKind.Write);
        var catchRead = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 7 &&
            access.Kind == Avm1RegisterAccessKind.Read);
        result.SymbolTable.TryGetRegisterSymbol(
            catchRead.Register,
            catchRead.Version,
            out var catchSymbol).ShouldBeTrue();

        catchWrite.Source.ShouldBe(constructed.Result);
        result.ValueAnalysis[catchRead.Value].Type.ShouldBe(Avm1InferredType.Object);
        result.ValueOrigins[constructed.Result].ShouldBe(Avm1ValueOrigin.ConstructedObject("Error"));
        result.ValueOrigins[catchRead.Value].ShouldBe(Avm1ValueOrigin.ConstructedObject("Error"));
        catchSymbol.Type.ShouldBe(Avm1InferredType.Object);
        catchSymbol.Name.ShouldBe("error");
        var as2 = result.GetStructuredAs2Text();
        as2.ShouldContain(" = new Error();");
        as2.ShouldContain("throw v");
        as2.ShouldContain("catch (error)");
        as2.ShouldContain("caught = error;");
        as2.ShouldNotContain("catch (_loc7_)");
    }

    [Fact]
    public void Action_try_shares_register_ssa_across_catch_and_finally()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("before")]),
                new ActionPush([PushValue.Integer(0)]),
                new ActionSetVariable(),
                new ActionPush([PushValue.Integer(2)]),
                new ActionStoreRegister(5),
                new ActionPop(),
                new ActionPush([PushValue.String("afterProbe")]),
                new ActionPush([PushValue.Integer(0)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(3)]),
                new ActionStoreRegister(5),
                new ActionPop()
            ],
            swfVersion: 6);
        var finallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Register(5)]),
                new ActionPush([PushValue.Integer(1)]),
                new ActionAdd2(),
                new ActionStoreRegister(5),
                new ActionPop()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionTry(
                TryFlags.CatchBlock | TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: "error",
                tryBody,
                catchBody,
                finallyBody),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var finallyEntry = result.ControlFlowGraph.Blocks.Single(block =>
            result.Instructions[block.StartAction].Kind == Avm1InstructionKind.FinallyEnter).Index;

        result.RegisterSsa.PhiNodes.ShouldContain(phi =>
            phi.Register == 5 &&
            phi.Block == finallyEntry);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "try",
            "{",
            "    before = 0;",
            "    var _loc5_ = 2;",
            "    afterProbe = 0;",
            "}",
            "catch (error)",
            "{",
            "    _loc5_ = 3;",
            "}",
            "finally",
            "{",
            "    _loc5_ += 1;",
            "}",
            "after = _loc5_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_nested_action_try_regions()
    {
        var innerTryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("x")]),
                new ActionPush([PushValue.Integer(1)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        var innerCatchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("innerCaught")]),
                new ActionPush([PushValue.Boolean(true)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        var innerTry = new ActionTry(
            TryFlags.CatchBlock,
            catchRegister: 0,
            catchVariable: "innerError",
            innerTryBody,
            innerCatchBody,
            ReadOnlyMemory<byte>.Empty);
        var outerTryBody = Avm1Action.EncodeCollection(
            [
                innerTry,
                new ActionPush([PushValue.String("y")]),
                new ActionPush([PushValue.Integer(2)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        var outerFinallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("cleanup")]),
                new ActionPush([PushValue.Boolean(true)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                outerTryBody,
                ReadOnlyMemory<byte>.Empty,
                outerFinallyBody),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.Instructions.TryRegions.Count.ShouldBe(2);
        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.Try).ShouldBe(2);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "try",
            "{",
            "    try",
            "    {",
            "        x = 1;",
            "    }",
            "    catch (innerError)",
            "    {",
            "        innerCaught = true;",
            "    }",
            "    y = 2;",
            "}",
            "finally",
            "{",
            "    cleanup = true;",
            "}",
            "z = 3;",
            string.Empty
        ]));
    }

    [Fact]
    public void Try_finally_preserves_finally_after_terminal_try_body()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(1)]),
                new ActionReturn()
            ],
            swfVersion: 6);
        var finallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("cleanup")]),
                new ActionPush([PushValue.Boolean(true)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                tryBody,
                ReadOnlyMemory<byte>.Empty,
                finallyBody)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "try",
            "{",
            "    return 1;",
            "}",
            "finally",
            "{",
            "    cleanup = true;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Return_completion_carries_register_state_into_finally()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(2)]),
                new ActionStoreRegister(5),
                new ActionPop(),
                new ActionPush([PushValue.Register(5)]),
                new ActionReturn()
            ],
            swfVersion: 6);
        var finallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("seen")]),
                new ActionPush([PushValue.Register(5)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                tryBody,
                ReadOnlyMemory<byte>.Empty,
                finallyBody)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        var returnAction = new ActionIndex(region.TryBodyStartAction.Value + 4);
        result.ControlFlowGraph.TryGetBlockForAction(returnAction, out var returnBlock).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(region.FinallyEnterAction, out var finallyEnter).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(region.FinallyExitAction, out var finallyExit).ShouldBeTrue();

        result.ControlFlowGraph.GetCompletionEdges(returnBlock).ToArray().ShouldBe([
            new Avm1CompletionEdge(
                Avm1CompletionKind.Return,
                finallyEnter,
                BlockIndex.Invalid)
        ]);
        result.ControlFlowGraph.GetCompletionEdges(finallyExit).ToArray().ShouldBe([
            new Avm1CompletionEdge(
                Avm1CompletionKind.Return,
                BlockIndex.Invalid,
                BlockIndex.Invalid)
        ]);

        var tryWrite = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Write &&
            access.Action == new ActionIndex(region.TryBodyStartAction.Value + 1));
        var finallyRead = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Read &&
            access.Action.Value >= region.FinallyBodyStartAction.Value);
        finallyRead.Version.ShouldBe(tryWrite.Version);
        result.GetStructuredAs2Text().ShouldContain("return 2;");
        result.GetStructuredAs2Text().ShouldContain("seen = 2;");
    }

    [Fact]
    public void Return_completion_crosses_nested_finally_regions_in_order()
    {
        var innerTryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(2)]),
                new ActionStoreRegister(5),
                new ActionPop(),
                new ActionPush([PushValue.Register(5)]),
                new ActionReturn()
            ],
            swfVersion: 6);
        var innerFinallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(3)]),
                new ActionStoreRegister(5),
                new ActionPop()
            ],
            swfVersion: 6);
        var innerTry = new ActionTry(
            TryFlags.FinallyBlock,
            catchRegister: 0,
            catchVariable: string.Empty,
            innerTryBody,
            ReadOnlyMemory<byte>.Empty,
            innerFinallyBody);
        var outerFinallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("seen")]),
                new ActionPush([PushValue.Register(5)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                Avm1Action.EncodeCollection([innerTry], swfVersion: 6),
                ReadOnlyMemory<byte>.Empty,
                outerFinallyBody)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var outer = result.Instructions.TryRegions.MaxBy(region =>
            region.TryExitAction.Value - region.EnterAction.Value);
        var inner = result.Instructions.TryRegions.MinBy(region =>
            region.TryExitAction.Value - region.EnterAction.Value);
        var returnAction = new ActionIndex(inner.TryBodyStartAction.Value + 4);
        result.ControlFlowGraph.TryGetBlockForAction(returnAction, out var returnBlock).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(inner.FinallyEnterAction, out var innerFinallyEnter).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(inner.FinallyExitAction, out var innerFinallyExit).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(outer.FinallyEnterAction, out var outerFinallyEnter).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(outer.FinallyExitAction, out var outerFinallyExit).ShouldBeTrue();

        result.ControlFlowGraph.GetCompletionEdges(returnBlock).ToArray().ShouldContain(
            new Avm1CompletionEdge(
                Avm1CompletionKind.Return,
                innerFinallyEnter,
                BlockIndex.Invalid));
        result.ControlFlowGraph.GetCompletionEdges(innerFinallyExit).ToArray().ShouldContain(
            new Avm1CompletionEdge(
                Avm1CompletionKind.Return,
                outerFinallyEnter,
                BlockIndex.Invalid));
        result.ControlFlowGraph.GetCompletionEdges(outerFinallyExit).ToArray().ShouldContain(
            new Avm1CompletionEdge(
                Avm1CompletionKind.Return,
                BlockIndex.Invalid,
                BlockIndex.Invalid));

        var innerFinallyWrite = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Write &&
            access.Action == new ActionIndex(inner.FinallyBodyStartAction.Value + 1));
        var outerFinallyRead = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Read &&
            access.Action.Value >= outer.FinallyBodyStartAction.Value);
        outerFinallyRead.Version.ShouldBe(innerFinallyWrite.Version);

        var returnInstruction = result.TacIr.Instructions.Single(instruction =>
            instruction.Action == returnAction &&
            instruction.Op is Avm1TacOp.Return);
        var payload = result.CompletionSsa.Values.Single(value =>
            value.Kind is Avm1CompletionValueKind.Source &&
            value.CompletionKind is Avm1CompletionKind.Return &&
            value.OriginAction == returnAction);
        payload.SourceValue.ShouldBe(returnInstruction.Operand0);
        result.CompletionSsa.Flows.ShouldContain(flow =>
            flow.Source == returnBlock &&
            flow.Target == innerFinallyEnter &&
            flow.Kind == Avm1CompletionKind.Return &&
            flow.Payload == payload.Index);
        result.CompletionSsa.Flows.ShouldContain(flow =>
            flow.Source == innerFinallyExit &&
            flow.Target == outerFinallyEnter &&
            flow.Kind == Avm1CompletionKind.Return &&
            flow.Payload == payload.Index);
        result.CompletionSsa.Flows.ShouldContain(flow =>
            flow.Source == outerFinallyExit &&
            !flow.Target.IsValid &&
            flow.Kind == Avm1CompletionKind.Return &&
            flow.Payload == payload.Index);
        result.GetStructuredAs2Text().ShouldContain("seen = 3;");
    }

    [Fact]
    public void Return_payloads_merge_explicitly_before_shared_finally()
    {
        var tryBodyActions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Register(1)]),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionReturn(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionReturn()
        };
        var offsets = ComputeOffsets(tryBodyActions);
        SetBranch(tryBodyActions, index: 1, offsets[4]);
        Avm1Action[] actions =
        [
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                Avm1Action.EncodeCollection(tryBodyActions, swfVersion: 6),
                ReadOnlyMemory<byte>.Empty,
                Avm1Action.EncodeCollection(
                    [
                        new ActionPush([PushValue.String("cleanup")]),
                        new ActionPush([PushValue.Boolean(true)]),
                        new ActionSetVariable()
                    ],
                    swfVersion: 6))
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        result.ControlFlowGraph.TryGetBlockForAction(region.FinallyEnterAction, out var finallyEnter).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(region.FinallyExitAction, out var finallyExit).ShouldBeTrue();

        var phi = result.CompletionSsa.PhiNodes.Single(node =>
            node.Block == finallyEnter &&
            node.State is Avm1FlowState.Return);
        var inputs = result.CompletionSsa.GetPhiInputs(phi).ToArray();
        inputs.Length.ShouldBe(2);
        var incomingDisplays = inputs
            .Select(input => result.CompletionSsa.Values[input.Value.Value])
            .Select(value => result.StackIr.Values[value.SourceValue.Value].Display)
            .Order()
            .ToArray();
        incomingDisplays.ShouldBe(["1", "2"]);
        result.CompletionSsa.Flows.ShouldContain(flow =>
            flow.Source == finallyExit &&
            !flow.Target.IsValid &&
            flow.Kind == Avm1CompletionKind.Return &&
            flow.Payload == phi.Result);
    }

    [Fact]
    public void Local_catch_after_nested_finally_restores_suspended_return()
    {
        var innerTry = new ActionTry(
            TryFlags.FinallyBlock,
            catchRegister: 0,
            catchVariable: string.Empty,
            Avm1Action.EncodeCollection(
                [
                    new ActionPush([PushValue.Integer(99)]),
                    new ActionPush([PushValue.String("inner")]),
                    new ActionThrow()
                ],
                swfVersion: 6),
            ReadOnlyMemory<byte>.Empty,
            Avm1Action.EncodeCollection(
                [
                    new ActionPush([PushValue.String("cleanup")]),
                    new ActionPush([PushValue.Boolean(true)]),
                    new ActionSetVariable()
                ],
                swfVersion: 6));
        var middleTry = new ActionTry(
            TryFlags.CatchBlock,
            catchRegister: 0,
            catchVariable: "error",
            Avm1Action.EncodeCollection([innerTry], swfVersion: 6),
            Avm1Action.EncodeCollection(
                [
                    new ActionPush([PushValue.String("caught")]),
                    new ActionPush([PushValue.Boolean(true)]),
                    new ActionSetVariable()
                ],
                swfVersion: 6),
            ReadOnlyMemory<byte>.Empty);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("restored")]),
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                Avm1Action.EncodeCollection(
                    [
                        new ActionPush([PushValue.Integer(42)]),
                        new ActionReturn()
                    ],
                    swfVersion: 6),
                ReadOnlyMemory<byte>.Empty,
                Avm1Action.EncodeCollection(
                    [
                        middleTry,
                        new ActionPush([PushValue.Integer(7)]),
                        new ActionSetVariable()
                    ],
                    swfVersion: 6))
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var outer = result.Instructions.TryRegions
            .Where(region => region.FinallyEnterAction.IsValid)
            .MaxBy(region => region.FinallyExitAction.Value - region.EnterAction.Value);
        var inner = result.Instructions.TryRegions
            .Where(region => region.FinallyEnterAction.IsValid)
            .MinBy(region => region.FinallyExitAction.Value - region.EnterAction.Value);
        var middle = result.Instructions.TryRegions.Single(region => region.CatchEnterAction.IsValid);
        var returnAction = new ActionIndex(outer.TryBodyStartAction.Value + 1);
        var throwAction = new ActionIndex(inner.TryBodyStartAction.Value + 2);
        result.ControlFlowGraph.TryGetBlockForAction(outer.FinallyExitAction, out var outerFinallyExit).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(inner.FinallyExitAction, out var innerFinallyExit).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(middle.CatchEnterAction, out var middleCatch).ShouldBeTrue();

        var returnPayload = result.CompletionSsa.Values.Single(value =>
            value.Kind is Avm1CompletionValueKind.Source &&
            value.CompletionKind is Avm1CompletionKind.Return &&
            value.OriginAction == returnAction);
        var throwPayload = result.CompletionSsa.Values.Single(value =>
            value.Kind is Avm1CompletionValueKind.Source &&
            value.CompletionKind is Avm1CompletionKind.Throw &&
            value.OriginAction == throwAction);
        result.CompletionSsa.Flows.ShouldContain(flow =>
            flow.Source == innerFinallyExit &&
            flow.Target == middleCatch &&
            flow.Kind == Avm1CompletionKind.Throw &&
            flow.Payload == throwPayload.Index &&
            flow.SourceState == Avm1FlowState.Throw &&
            flow.TargetState == Avm1FlowState.Return);
        result.CompletionSsa.Flows.ShouldContain(flow =>
            flow.Source == outerFinallyExit &&
            !flow.Target.IsValid &&
            flow.Kind == Avm1CompletionKind.Return &&
            flow.Payload == returnPayload.Index);

        var resumedTransition = result.FlowGraph.Points
            .SelectMany(point => result.FlowGraph.GetTransitions(point.Index)
                .ToArray()
                .Select(transition => (Point: point, Transition: transition)))
            .Single(item =>
                item.Point.Block == innerFinallyExit &&
                item.Transition.Kind == Avm1FlowTransitionKind.Completion &&
                item.Transition.CompletionKind == Avm1CompletionKind.Throw &&
                item.Transition.Target.IsValid &&
                result.FlowGraph[item.Transition.Target].Block == middleCatch &&
                result.FlowGraph.GetContext(item.Transition.TargetContext).State == Avm1FlowState.Return);
        var suspendedThrow = result.FlowGraph.GetContext(resumedTransition.Point.Context);
        var restoredReturn = result.FlowGraph.GetContext(resumedTransition.Transition.TargetContext);
        suspendedThrow.State.ShouldBe(Avm1FlowState.Throw);
        suspendedThrow.Parent.ShouldBe(restoredReturn.Index);
        restoredReturn.State.ShouldBe(Avm1FlowState.Return);
        result.StackDepthAnalysis.GetEntryDepth(middleCatch, Avm1FlowState.Return).ShouldBe(1);
        result.StackDepthAnalysis.HasKnownEntryForState(middleCatch, Avm1FlowState.Normal).ShouldBeFalse();

        var restoredAssignment = result.TacIr.Instructions.Single(instruction =>
            instruction.Op == Avm1TacOp.SetVariable &&
            instruction.Action.Value > middle.CatchExitAction.Value);
        result.StackIr.Values[restoredAssignment.Operand0.Value].Display.ShouldBe("\"restored\"");
        result.StackIr.Values[restoredAssignment.Operand1.Value].Display.ShouldBe("7");
        result.GetStructuredAs2Text().ShouldContain("return 42;");
        result.GetStructuredAs2Text().ShouldContain("restored = 7;");
    }

    [Fact]
    public void Local_catch_after_nested_finally_restores_suspended_throw()
    {
        var innerTry = new ActionTry(
            TryFlags.FinallyBlock,
            catchRegister: 0,
            catchVariable: string.Empty,
            Avm1Action.EncodeCollection(
                [
                    new ActionPush([PushValue.String("inner")]),
                    new ActionThrow()
                ],
                swfVersion: 6),
            ReadOnlyMemory<byte>.Empty,
            Avm1Action.EncodeCollection(
                [
                    new ActionPush([PushValue.String("cleanup")]),
                    new ActionPush([PushValue.Boolean(true)]),
                    new ActionSetVariable()
                ],
                swfVersion: 6));
        var middleTry = new ActionTry(
            TryFlags.CatchBlock,
            catchRegister: 0,
            catchVariable: "error",
            Avm1Action.EncodeCollection([innerTry], swfVersion: 6),
            Avm1Action.EncodeCollection(
                [
                    new ActionPush([PushValue.String("caught")]),
                    new ActionPush([PushValue.Boolean(true)]),
                    new ActionSetVariable()
                ],
                swfVersion: 6),
            ReadOnlyMemory<byte>.Empty);
        Avm1Action[] actions =
        [
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                Avm1Action.EncodeCollection(
                    [
                        new ActionPush([PushValue.String("outer")]),
                        new ActionThrow()
                    ],
                    swfVersion: 6),
                ReadOnlyMemory<byte>.Empty,
                Avm1Action.EncodeCollection([middleTry], swfVersion: 6))
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var outer = result.Instructions.TryRegions
            .Where(region => region.FinallyEnterAction.IsValid)
            .MaxBy(region => region.FinallyExitAction.Value - region.EnterAction.Value);
        var inner = result.Instructions.TryRegions
            .Where(region => region.FinallyEnterAction.IsValid)
            .MinBy(region => region.FinallyExitAction.Value - region.EnterAction.Value);
        var middle = result.Instructions.TryRegions.Single(region => region.CatchEnterAction.IsValid);
        var outerThrowAction = new ActionIndex(outer.TryBodyStartAction.Value + 1);
        var innerThrowAction = new ActionIndex(inner.TryBodyStartAction.Value + 1);
        result.ControlFlowGraph.TryGetBlockForAction(outer.FinallyExitAction, out var outerFinallyExit).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(inner.FinallyExitAction, out var innerFinallyExit).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(middle.CatchEnterAction, out var middleCatch).ShouldBeTrue();

        var outerPayload = result.CompletionSsa.Values.Single(value =>
            value.Kind == Avm1CompletionValueKind.Source &&
            value.CompletionKind == Avm1CompletionKind.Throw &&
            value.OriginAction == outerThrowAction);
        var innerPayload = result.CompletionSsa.Values.Single(value =>
            value.Kind == Avm1CompletionValueKind.Source &&
            value.CompletionKind == Avm1CompletionKind.Throw &&
            value.OriginAction == innerThrowAction);
        var innerDelivery = result.CompletionSsa.Flows.Single(flow =>
            flow.Source == innerFinallyExit &&
            flow.Target == middleCatch &&
            flow.Kind == Avm1CompletionKind.Throw &&
            flow.Payload == innerPayload.Index &&
            flow.TargetState == Avm1FlowState.Throw);
        var suspendedInnerThrow = result.FlowGraph.GetContext(innerDelivery.SourceContext);
        var restoredOuterThrow = result.FlowGraph.GetContext(innerDelivery.TargetContext);
        suspendedInnerThrow.Parent.ShouldBe(restoredOuterThrow.Index);
        suspendedInnerThrow.Depth.ShouldBe(restoredOuterThrow.Depth + 1);
        restoredOuterThrow.State.ShouldBe(Avm1FlowState.Throw);

        result.CompletionSsa.Flows.ShouldContain(flow =>
            flow.Source == outerFinallyExit &&
            !flow.Target.IsValid &&
            flow.Kind == Avm1CompletionKind.Throw &&
            flow.Payload == outerPayload.Index &&
            flow.SourceContext == restoredOuterThrow.Index);
        result.StackDepthAnalysis.HasKnownEntryForState(middleCatch, Avm1FlowState.Throw).ShouldBeTrue();
        result.GetStructuredAs2Text().ShouldContain("throw \"outer\";");
        result.GetStructuredAs2Text().ShouldContain("caught = true;");
    }

    [Fact]
    public void Finally_exit_keeps_return_state_out_of_normal_continuation_ssa()
    {
        var tryBodyActions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Register(1)]),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn()
        };
        var offsets = ComputeOffsets(tryBodyActions);
        SetBranch(tryBodyActions, index: 1, offsets[6]);
        SetBranch(tryBodyActions, index: 5, tryBodyActions.Sum(Len));
        var finallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("observed")]),
                new ActionPush([PushValue.Register(5)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                Avm1Action.EncodeCollection(tryBodyActions, swfVersion: 6),
                ReadOnlyMemory<byte>.Empty,
                finallyBody),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        result.ControlFlowGraph.TryGetBlockForAction(region.FinallyEnterAction, out var finallyEnter).ShouldBeTrue();
        var normalWrite = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Write &&
            access.Action == new ActionIndex(region.TryBodyStartAction.Value + 3));
        var finallyRead = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Read &&
            access.Action == new ActionIndex(region.FinallyBodyStartAction.Value + 1));
        var continuationRead = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Read &&
            access.Action == new ActionIndex(region.ContinuationAction.Value + 1));
        result.ControlFlowGraph.TryGetBlockForAction(
            finallyRead.Action,
            out var finallyReadBlock).ShouldBeTrue();
        var finallyPhi = result.RegisterSsa.PhiNodes.Single(phi =>
            phi.Block == finallyReadBlock &&
            phi.Register == 5 &&
            phi.State == Avm1FlowState.Merged);

        finallyRead.Version.ShouldBe(finallyPhi.Version);
        continuationRead.Version.ShouldBe(normalWrite.Version);
        continuationRead.Version.ShouldNotBe(finallyRead.Version);
        result.GetStructuredAs2Text().ShouldContain("after = 2;");
    }

    [Fact]
    public void Finally_exit_keeps_throw_state_out_of_normal_continuation_ssa()
    {
        var tryBodyActions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Register(1)]),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("boom")]),
            new ActionThrow()
        };
        var offsets = ComputeOffsets(tryBodyActions);
        SetBranch(tryBodyActions, index: 1, offsets[6]);
        SetBranch(tryBodyActions, index: 5, tryBodyActions.Sum(Len));
        var finallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("observed")]),
                new ActionPush([PushValue.Register(5)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                Avm1Action.EncodeCollection(tryBodyActions, swfVersion: 6),
                ReadOnlyMemory<byte>.Empty,
                finallyBody),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        var normalWrite = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Write &&
            access.Action == new ActionIndex(region.TryBodyStartAction.Value + 3));
        var finallyRead = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Read &&
            access.Action == new ActionIndex(region.FinallyBodyStartAction.Value + 1));
        var continuationRead = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Read &&
            access.Action == new ActionIndex(region.ContinuationAction.Value + 1));
        result.ControlFlowGraph.TryGetBlockForAction(
            finallyRead.Action,
            out var finallyReadBlock).ShouldBeTrue();
        var finallyPhi = result.RegisterSsa.PhiNodes.Single(phi =>
            phi.Block == finallyReadBlock &&
            phi.Register == 5 &&
            phi.State == Avm1FlowState.Merged);

        finallyPhi.IncomingStates.Distinct().Order().ShouldBe([
            Avm1FlowState.Normal,
            Avm1FlowState.Throw
        ]);
        finallyRead.Version.ShouldBe(finallyPhi.Version);
        continuationRead.Version.ShouldBe(normalWrite.Version);
        continuationRead.Version.ShouldNotBe(finallyRead.Version);
        result.GetStructuredAs2Text().ShouldContain("after = 2;");
    }

    [Fact]
    public void Finally_exit_keeps_return_stack_out_of_normal_continuation()
    {
        var tryBodyActions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Register(1)]),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionReturn()
        };
        var offsets = ComputeOffsets(tryBodyActions);
        SetBranch(tryBodyActions, index: 1, offsets[4]);
        SetBranch(tryBodyActions, index: 3, tryBodyActions.Sum(Len));
        var finallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("cleanup")]),
                new ActionPush([PushValue.Boolean(true)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("after")]),
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                Avm1Action.EncodeCollection(tryBodyActions, swfVersion: 6),
                ReadOnlyMemory<byte>.Empty,
                finallyBody),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        result.ControlFlowGraph.TryGetBlockForAction(region.FinallyEnterAction, out var finallyEnter).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(region.ContinuationAction, out var continuation).ShouldBeTrue();

        result.StackDepthAnalysis.GetEntryDepth(finallyEnter, Avm1FlowState.Normal).ShouldBe(2);
        result.StackDepthAnalysis.GetEntryDepth(finallyEnter, Avm1FlowState.Return).ShouldBe(1);
        result.StackDepthAnalysis.GetEntryDepth(continuation, Avm1FlowState.Normal).ShouldBe(2);
        result.StackDepthAnalysis.HasKnownEntryForState(continuation, Avm1FlowState.Return).ShouldBeFalse();

        var assignment = result.TacIr.Instructions.Single(instruction =>
            instruction.Action == region.ContinuationAction &&
            instruction.Op is Avm1TacOp.SetVariable);
        result.StackIr.Values[assignment.Operand0.Value].Display.ShouldBe("\"after\"");
        result.StackIr.Values[assignment.Operand1.Value].Display.ShouldBe("2");
        result.StackIr.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Message.Contains("Inconsistent stack depth", StringComparison.Ordinal));
        result.GetStructuredAs2Text().ShouldContain("after = 2;");
    }

    [Fact]
    public void Shared_finally_merges_stack_for_its_body_but_projects_normal_exit()
    {
        var tryBodyActions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Register(1)]),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(9)]),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn()
        };
        var offsets = ComputeOffsets(tryBodyActions);
        SetBranch(tryBodyActions, index: 1, offsets[4]);
        SetBranch(tryBodyActions, index: 3, tryBodyActions.Sum(Len));
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("after")]),
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                Avm1Action.EncodeCollection(tryBodyActions, swfVersion: 6),
                ReadOnlyMemory<byte>.Empty,
                Avm1Action.EncodeCollection(
                    [
                        new ActionPush([PushValue.String("cleanup")]),
                        new ActionPush([PushValue.Boolean(true)]),
                        new ActionSetVariable()
                    ],
                    swfVersion: 6)),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        result.ControlFlowGraph.TryGetBlockForAction(region.FinallyEnterAction, out var finallyEnter).ShouldBeTrue();
        var phi = result.StackIr.PhiNodes.Single(node =>
            node.Block == finallyEnter &&
            node.State == Avm1FlowState.Merged &&
            node.Slot == 1);

        phi.IncomingStates.Distinct().Order().ShouldBe([
            Avm1FlowState.Normal,
            Avm1FlowState.Return
        ]);
        phi.IncomingValues
            .Select(value => result.StackIr.Values[value.Value].Display)
            .Order()
            .ShouldBe(["2", "9"]);

        var assignment = result.TacIr.Instructions.Single(instruction =>
            instruction.Action == region.ContinuationAction &&
            instruction.Op is Avm1TacOp.SetVariable);
        assignment.Operand1.ShouldNotBe(phi.Result);
        result.StackIr.Values[assignment.Operand1.Value].Display.ShouldBe("2");
        result.GetStructuredAs2Text().ShouldContain("after = 2;");
    }

    [Fact]
    public void Shared_finally_projects_transformed_stack_value_to_normal_continuation()
    {
        var tryBodyActions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Register(1)]),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(9)]),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn()
        };
        var offsets = ComputeOffsets(tryBodyActions);
        SetBranch(tryBodyActions, index: 1, offsets[4]);
        SetBranch(tryBodyActions, index: 3, tryBodyActions.Sum(Len));
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("after")]),
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                Avm1Action.EncodeCollection(tryBodyActions, swfVersion: 6),
                ReadOnlyMemory<byte>.Empty,
                Avm1Action.EncodeCollection(
                    [
                        new ActionPush([PushValue.Integer(1)]),
                        new ActionAdd2()
                    ],
                    swfVersion: 6)),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        var add = result.TacIr.Instructions.Single(instruction =>
            instruction.Action == new ActionIndex(region.FinallyBodyStartAction.Value + 1) &&
            instruction.Op is Avm1TacOp.Binary &&
            instruction.Flags is Avm1IrInstructionFlags.None);
        var projectedAdds = result.TacIr.Instructions.Where(instruction =>
            instruction.Action == add.Action &&
            instruction.Op is Avm1TacOp.Binary &&
            (instruction.Flags & Avm1IrInstructionFlags.ContextProjection) != 0).ToArray();
        var normalProjection = projectedAdds.Single(instruction =>
            result.FlowGraph.GetContext(instruction.Context).State is Avm1FlowState.Normal);
        var aggregatePhi = result.StackIr.PhiNodes.Single(node =>
            node.Result == add.Operand0 &&
            node.State == Avm1FlowState.Merged);
        var assignment = result.TacIr.Instructions.Single(instruction =>
            instruction.Action == region.ContinuationAction &&
            instruction.Op is Avm1TacOp.SetVariable);

        add.Operand0.ShouldBe(aggregatePhi.Result);
        aggregatePhi.IncomingValues
            .Select(value => result.StackIr.Values[value.Value].Display)
            .Order()
            .ShouldBe(["2", "9"]);
        projectedAdds.Length.ShouldBe(2);
        assignment.Operand1.ShouldBe(normalProjection.Result);
        assignment.Operand1.ShouldNotBe(add.Result);
        result.ValueAnalysis[assignment.Operand1].ConstantKind.ShouldBe(Avm1ConstantKind.Integer);
        result.ValueAnalysis[assignment.Operand1].IntegerValue.ShouldBe(3);
        result.GetStructuredAs2Text().ShouldContain("after = 3;");
        result.GetStructuredAs2Text().ShouldNotContain("phi(");
    }

    [Fact]
    public void Shared_finally_does_not_replay_dynamic_coercion_as_context_projection()
    {
        var tryBodyActions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Register(1)]),
            new ActionIf(0),
            new ActionPush([PushValue.Register(2)]),
            new ActionJump(0),
            new ActionPush([PushValue.Register(3)]),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn()
        };
        var offsets = ComputeOffsets(tryBodyActions);
        SetBranch(tryBodyActions, index: 1, offsets[4]);
        SetBranch(tryBodyActions, index: 3, tryBodyActions.Sum(Len));
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("after")]),
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                Avm1Action.EncodeCollection(tryBodyActions, swfVersion: 6),
                ReadOnlyMemory<byte>.Empty,
                Avm1Action.EncodeCollection(
                    [
                        new ActionPush([PushValue.Integer(1)]),
                        new ActionAdd2()
                    ],
                    swfVersion: 6)),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        var addAction = new ActionIndex(region.FinallyBodyStartAction.Value + 1);
        var adds = result.TacIr.Instructions.Where(instruction =>
            instruction.Action == addAction &&
            instruction.Op is Avm1TacOp.Binary).ToArray();
        var assignment = result.TacIr.Instructions.Single(instruction =>
            instruction.Action == region.ContinuationAction &&
            instruction.Op is Avm1TacOp.SetVariable);

        adds.Length.ShouldBe(1);
        adds[0].Flags.ShouldBe(Avm1IrInstructionFlags.None);
        assignment.Operand1.ShouldBe(adds[0].Result);
        result.StackIr.Values.ShouldNotContain(value =>
            value.Kind == Avm1StackValueKind.Projection);
    }

    [Fact]
    public void Finally_exit_keeps_throw_stack_out_of_normal_continuation()
    {
        var tryBodyActions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Register(1)]),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionJump(0),
            new ActionPush([PushValue.String("boom")]),
            new ActionThrow()
        };
        var offsets = ComputeOffsets(tryBodyActions);
        SetBranch(tryBodyActions, index: 1, offsets[4]);
        SetBranch(tryBodyActions, index: 3, tryBodyActions.Sum(Len));
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("after")]),
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                Avm1Action.EncodeCollection(tryBodyActions, swfVersion: 6),
                ReadOnlyMemory<byte>.Empty,
                Avm1Action.EncodeCollection(
                    [
                        new ActionPush([PushValue.String("cleanup")]),
                        new ActionPush([PushValue.Boolean(true)]),
                        new ActionSetVariable()
                    ],
                    swfVersion: 6)),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        result.ControlFlowGraph.TryGetBlockForAction(region.FinallyEnterAction, out var finallyEnter).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(region.ContinuationAction, out var continuation).ShouldBeTrue();

        result.StackDepthAnalysis.GetEntryDepth(finallyEnter, Avm1FlowState.Normal).ShouldBe(2);
        result.StackDepthAnalysis.GetEntryDepth(finallyEnter, Avm1FlowState.Throw).ShouldBe(1);
        result.StackDepthAnalysis.GetEntryDepth(continuation, Avm1FlowState.Normal).ShouldBe(2);
        result.StackDepthAnalysis.HasKnownEntryForState(continuation, Avm1FlowState.Throw).ShouldBeFalse();

        var assignment = result.TacIr.Instructions.Single(instruction =>
            instruction.Action == region.ContinuationAction &&
            instruction.Op is Avm1TacOp.SetVariable);
        result.StackIr.Values[assignment.Operand0.Value].Display.ShouldBe("\"after\"");
        result.StackIr.Values[assignment.Operand1.Value].Display.ShouldBe("2");
        result.GetStructuredAs2Text().ShouldContain("after = 2;");
    }

    [Fact]
    public void Pending_throw_completion_leaves_inner_finally_for_outer_catch()
    {
        var innerTryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(2)]),
                new ActionStoreRegister(5),
                new ActionPop(),
                new ActionPush([PushValue.String("boom")]),
                new ActionThrow()
            ],
            swfVersion: 6);
        var innerFinallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(3)]),
                new ActionStoreRegister(5),
                new ActionPop()
            ],
            swfVersion: 6);
        var innerTry = new ActionTry(
            TryFlags.FinallyBlock,
            catchRegister: 0,
            catchVariable: string.Empty,
            innerTryBody,
            ReadOnlyMemory<byte>.Empty,
            innerFinallyBody);
        var outerCatchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("seen")]),
                new ActionPush([PushValue.Register(5)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("sentinel")]),
            new ActionTry(
                TryFlags.CatchBlock,
                catchRegister: 0,
                catchVariable: "error",
                Avm1Action.EncodeCollection([innerTry], swfVersion: 6),
                outerCatchBody,
                ReadOnlyMemory<byte>.Empty)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var outer = result.Instructions.TryRegions.MaxBy(region =>
            region.TryExitAction.Value - region.EnterAction.Value);
        var inner = result.Instructions.TryRegions.MinBy(region =>
            region.TryExitAction.Value - region.EnterAction.Value);
        result.ControlFlowGraph.TryGetBlockForAction(inner.FinallyExitAction, out var innerFinallyExit).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(inner.FinallyEnterAction, out var innerFinallyEnter).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(outer.CatchEnterAction, out var outerCatchEnter).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(outer.EnterAction, out var outerHeader).ShouldBeTrue();

        result.ControlFlowGraph.GetCompletionEdges(innerFinallyExit).ToArray().ShouldContain(
            new Avm1CompletionEdge(
                Avm1CompletionKind.Throw,
                outerCatchEnter,
                outerHeader));

        var finallyWrite = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Write &&
            access.Action == new ActionIndex(inner.FinallyBodyStartAction.Value + 1));
        var catchRead = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Read &&
            access.Action.Value >= outer.CatchBodyStartAction.Value);
        catchRead.Version.ShouldBe(finallyWrite.Version);

        var throwAction = new ActionIndex(inner.TryBodyStartAction.Value + 4);
        result.ControlFlowGraph.TryGetBlockForAction(throwAction, out var throwBlock).ShouldBeTrue();
        var throwInstruction = result.TacIr.Instructions.Single(instruction =>
            instruction.Action == throwAction &&
            instruction.Op is Avm1TacOp.Throw);
        var payload = result.CompletionSsa.Values.Single(value =>
            value.Kind is Avm1CompletionValueKind.Source &&
            value.CompletionKind is Avm1CompletionKind.Throw &&
            value.OriginAction == throwAction);
        payload.SourceValue.ShouldBe(throwInstruction.Operand0);
        result.CompletionSsa.Flows.ShouldContain(flow =>
            flow.Source == throwBlock &&
            flow.Target == innerFinallyEnter &&
            flow.EdgeKind == Avm1AbruptFlowEdgeKind.Exception &&
            flow.Payload == payload.Index);
        result.CompletionSsa.Flows.ShouldContain(flow =>
            flow.Source == innerFinallyExit &&
            flow.Target == outerCatchEnter &&
            flow.EdgeKind == Avm1AbruptFlowEdgeKind.Completion &&
            flow.Payload == payload.Index);
        result.StackDepthAnalysis.EntryDepths[outerCatchEnter.Value].ShouldBe(1);
        result.GetStructuredAs2Text().ShouldContain("seen = 3;");
    }

    [Fact]
    public void Catch_observes_exact_register_version_from_explicit_throw_path()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(2)]),
                new ActionStoreRegister(5),
                new ActionPop(),
                new ActionPush([PushValue.String("boom")]),
                new ActionThrow()
            ],
            swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("seen")]),
                new ActionPush([PushValue.Register(5)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionTry(
                TryFlags.CatchBlock,
                catchRegister: 0,
                catchVariable: "error",
                tryBody,
                catchBody,
                ReadOnlyMemory<byte>.Empty)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var catchEntry = result.ControlFlowGraph.Blocks.Single(block =>
            result.Instructions[block.StartAction].Kind == Avm1InstructionKind.CatchEnter).Index;
        var tryWrite = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Write &&
            result.TacIr[access.Instruction].Op == Avm1TacOp.StoreRegister &&
            access.Version > 1);
        var catchRead = result.RegisterSsa.Accesses.Single(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Read);

        result.RegisterSsa.PhiNodes.ShouldNotContain(phi =>
            phi.Block == catchEntry && phi.Register == 5);
        catchRead.Version.ShouldBe(tryWrite.Version);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "try",
            "{",
            "    throw \"boom\";",
            "}",
            "catch (error)",
            "{",
            "    seen = 2;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Catch_unwinds_operand_stack_to_action_try_entry()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("transient")]),
                new ActionPush([PushValue.String("boom")]),
                new ActionThrow()
            ],
            swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("seen")]),
                new ActionStackSwap(),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("sentinel")]),
            new ActionTry(
                TryFlags.CatchBlock,
                catchRegister: 0,
                catchVariable: "error",
                tryBody,
                catchBody,
                ReadOnlyMemory<byte>.Empty)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        result.ControlFlowGraph.TryGetBlockForAction(
            region.CatchEnterAction,
            out var catchEntry).ShouldBeTrue();

        result.StackDepthAnalysis.EntryDepths[catchEntry.Value].ShouldBe(1);
        result.StackDepthAnalysis.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Message.Contains("Inconsistent stack depth", StringComparison.Ordinal));
        result.StackIr.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Message.Contains("Inconsistent stack depth", StringComparison.Ordinal));
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "try",
            "{",
            "    throw \"boom\";",
            "}",
            "catch (error)",
            "{",
            "    seen = \"sentinel\";",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Finally_unwinds_operand_stack_to_action_try_entry()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("transient")]),
                new ActionPush([PushValue.String("maybe")]),
                new ActionGetVariable(),
                new ActionPop(),
                new ActionPop()
            ],
            swfVersion: 6);
        var finallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("seen")]),
                new ActionStackSwap(),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("sentinel")]),
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                tryBody,
                ReadOnlyMemory<byte>.Empty,
                finallyBody)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.Instructions.TryRegions.Single();
        result.ControlFlowGraph.TryGetBlockForAction(
            region.FinallyEnterAction,
            out var finallyEntry).ShouldBeTrue();

        result.StackDepthAnalysis.EntryDepths[finallyEntry.Value].ShouldBe(1);
        result.StackDepthAnalysis.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Message.Contains("Inconsistent stack depth", StringComparison.Ordinal));
        result.StackIr.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Message.Contains("Inconsistent stack depth", StringComparison.Ordinal));
    }

    [Fact]
    public void Catch_merges_register_versions_only_from_throwing_instructions()
    {
        var tryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("maybe")]),
                new ActionGetVariable(),
                new ActionPop(),
                new ActionPush([PushValue.Integer(2)]),
                new ActionStoreRegister(5),
                new ActionPop(),
                new ActionPush([PushValue.String("boom")]),
                new ActionThrow()
            ],
            swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("seen")]),
                new ActionPush([PushValue.Register(5)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionTry(
                TryFlags.CatchBlock,
                catchRegister: 0,
                catchVariable: "error",
                tryBody,
                catchBody,
                ReadOnlyMemory<byte>.Empty)
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var catchEntry = result.ControlFlowGraph.Blocks.Single(block =>
            result.Instructions[block.StartAction].Kind == Avm1InstructionKind.CatchEnter).Index;
        var phi = result.RegisterSsa.PhiNodes.Single(node =>
            node.Block == catchEntry && node.Register == 5);

        phi.IncomingVersions.Distinct().Order().ShouldBe([1, 2]);
        result.RegisterSsa.Accesses.ShouldContain(access =>
            access.Register == 5 &&
            access.Kind == Avm1RegisterAccessKind.Read &&
            access.Version == phi.Version);
    }

    [Fact]
    public void Nested_try_exception_edges_select_innermost_then_outer_handler()
    {
        var innerTryBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("inner")]),
                new ActionPush([PushValue.Integer(1)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        var innerFinallyBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("innerCleanup")]),
                new ActionPush([PushValue.Boolean(true)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        var innerTry = new ActionTry(
            TryFlags.FinallyBlock,
            catchRegister: 0,
            catchVariable: string.Empty,
            innerTryBody,
            ReadOnlyMemory<byte>.Empty,
            innerFinallyBody);
        var outerTryBody = Avm1Action.EncodeCollection([innerTry], swfVersion: 6);
        var outerCatchBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.String("outerCaught")]),
                new ActionPush([PushValue.Boolean(true)]),
                new ActionSetVariable()
            ],
            swfVersion: 6);
        Avm1Action[] actions =
        [
            new ActionTry(
                TryFlags.CatchBlock,
                catchRegister: 0,
                catchVariable: "error",
                outerTryBody,
                outerCatchBody,
                ReadOnlyMemory<byte>.Empty)
        ];

        var table = Avm1InstructionTable.Build(actions, swfVersion: 6);
        var cfg = Avm1ControlFlowGraph.Build(table);
        var outer = table.TryRegions.MaxBy(region =>
            region.TryExitAction.Value - region.EnterAction.Value);
        var inner = table.TryRegions.MinBy(region =>
            region.TryExitAction.Value - region.EnterAction.Value);

        var innerTrySet = new ActionIndex(inner.TryBodyStartAction.Value + 2);
        var innerFinallySet = new ActionIndex(inner.FinallyBodyStartAction.Value + 2);
        cfg.TryGetBlockForAction(inner.EnterAction, out var innerHeader).ShouldBeTrue();
        cfg.TryGetBlockForAction(innerTrySet, out var innerTryBlock).ShouldBeTrue();
        cfg.TryGetBlockForAction(inner.FinallyEnterAction, out var innerFinallyEnter).ShouldBeTrue();
        cfg.TryGetBlockForAction(innerFinallySet, out var innerFinallyBlock).ShouldBeTrue();
        cfg.TryGetBlockForAction(outer.EnterAction, out var outerHeader).ShouldBeTrue();
        cfg.TryGetBlockForAction(outer.CatchEnterAction, out var outerCatchEnter).ShouldBeTrue();

        cfg[innerTryBlock].ExceptionSuccessor.ShouldBe(innerFinallyEnter);
        cfg[innerTryBlock].ExceptionStackSource.ShouldBe(innerHeader);
        cfg[innerFinallyBlock].ExceptionSuccessor.ShouldBe(outerCatchEnter);
        cfg[innerFinallyBlock].ExceptionStackSource.ShouldBe(outerHeader);
    }

    [Fact]
    public void Builds_cfg_for_if_else_shape()
    {
        var thenBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var elseBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0)
        };
        actions.AddRange(thenBody);
        actions.Add(new ActionJump(0));
        actions.AddRange(elseBody);

        var offsets = ComputeOffsets(actions);
        var ifIndex = 2;
        var jumpIndex = ifIndex + 1 + thenBody.Length;
        var elseStart = jumpIndex + 1;
        var afterElse = elseStart + elseBody.Length;

        SetBranch(actions, ifIndex, offsets[elseStart]);
        SetBranch(actions, jumpIndex, offsets[^1] + Len(actions[^1]));

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var cfg = result.ControlFlowGraph;

        cfg.Count.ShouldBe(3);
        cfg.Blocks[0].Terminator.ShouldBe(Avm1BlockTerminatorKind.ConditionalBranch);
        cfg.Blocks[0].FirstSuccessor.ShouldBe(new BlockIndex(1));
        cfg.Blocks[0].SecondSuccessor.ShouldBe(new BlockIndex(2));
        cfg.Blocks[1].Terminator.ShouldBe(Avm1BlockTerminatorKind.Jump);
        cfg.Blocks[1].FirstSuccessor.ShouldBe(BlockIndex.Invalid);
        cfg.Blocks[2].Terminator.ShouldBe(Avm1BlockTerminatorKind.FallThrough);
        afterElse.ShouldBe(actions.Count);
    }

    [Fact]
    public void Builds_dominator_tree_for_if_else_shape()
    {
        var thenBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var elseBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0)
        };
        actions.AddRange(thenBody);
        actions.Add(new ActionJump(0));
        actions.AddRange(elseBody);

        var offsets = ComputeOffsets(actions);
        var ifIndex = 2;
        var jumpIndex = ifIndex + 1 + thenBody.Length;
        var elseStart = jumpIndex + 1;

        SetBranch(actions, ifIndex, offsets[elseStart]);
        SetBranch(actions, jumpIndex, offsets[^1] + Len(actions[^1]));

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var dominators = result.DominatorTree;

        dominators.ImmediateDominators[0].ShouldBe(BlockIndex.Invalid);
        dominators.ImmediateDominators[1].ShouldBe(new BlockIndex(0));
        dominators.ImmediateDominators[2].ShouldBe(new BlockIndex(0));
        dominators.Dominates(new BlockIndex(0), new BlockIndex(1)).ShouldBeTrue();
        dominators.Dominates(new BlockIndex(0), new BlockIndex(2)).ShouldBeTrue();
        dominators.Dominates(new BlockIndex(1), new BlockIndex(2)).ShouldBeFalse();
    }

    [Fact]
    public void Builds_post_dominator_tree_for_if_else_merge_shape()
    {
        var thenBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var elseBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var mergeBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        };

        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0)
        };
        actions.AddRange(thenBody);
        actions.Add(new ActionJump(0));
        actions.AddRange(elseBody);
        actions.AddRange(mergeBody);

        var offsets = ComputeOffsets(actions);
        var ifIndex = 2;
        var jumpIndex = ifIndex + 1 + thenBody.Length;
        var elseStart = jumpIndex + 1;
        var mergeStart = elseStart + elseBody.Length;

        SetBranch(actions, ifIndex, offsets[elseStart]);
        SetBranch(actions, jumpIndex, offsets[mergeStart]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var cfg = result.ControlFlowGraph;
        var postDominators = result.PostDominatorTree;
        var ifRegions = result.RegionAnalysis.IfRegions;

        cfg.Count.ShouldBe(4);
        postDominators.VirtualExit.ShouldBe(new BlockIndex(4));
        postDominators.ImmediatePostDominators[0].ShouldBe(new BlockIndex(3));
        postDominators.ImmediatePostDominators[1].ShouldBe(new BlockIndex(3));
        postDominators.ImmediatePostDominators[2].ShouldBe(new BlockIndex(3));
        postDominators.ImmediatePostDominators[3].ShouldBe(postDominators.VirtualExit);
        postDominators.PostDominates(new BlockIndex(3), new BlockIndex(0)).ShouldBeTrue();
        postDominators.PostDominates(new BlockIndex(3), new BlockIndex(1)).ShouldBeTrue();
        postDominators.PostDominates(new BlockIndex(3), new BlockIndex(2)).ShouldBeTrue();
        postDominators.PostDominates(postDominators.VirtualExit, new BlockIndex(3)).ShouldBeTrue();

        ifRegions.Count.ShouldBe(1);
        ifRegions[0].Header.ShouldBe(new BlockIndex(0));
        ifRegions[0].FallThroughEntry.ShouldBe(new BlockIndex(1));
        ifRegions[0].BranchEntry.ShouldBe(new BlockIndex(2));
        ifRegions[0].Merge.ShouldBe(new BlockIndex(3));
        ifRegions[0].HasElse.ShouldBeTrue();

        var ast = result.StructuredAst;
        ast.Root.IsValid.ShouldBeTrue();
        ast[ast.Root].Kind.ShouldBe(Avm1AstNodeKind.Root);
        var rootChildren = ast.GetChildren(ast[ast.Root]);
        rootChildren.Count.ShouldBe(2);
        ast[rootChildren[0]].Kind.ShouldBe(Avm1AstNodeKind.If);
        ast[rootChildren[0]].Block.ShouldBe(new BlockIndex(0));
        ast[rootChildren[0]].Merge.ShouldBe(new BlockIndex(3));
        ast[rootChildren[1]].Kind.ShouldBe(Avm1AstNodeKind.Block);
        ast[rootChildren[1]].Block.ShouldBe(new BlockIndex(3));

        var ifChildren = ast.GetChildren(ast[rootChildren[0]]);
        ifChildren.Count.ShouldBe(3);
        ast[ifChildren[0]].Kind.ShouldBe(Avm1AstNodeKind.Unary);
        ast[ifChildren[1]].Kind.ShouldBe(Avm1AstNodeKind.Block);
        ast[ifChildren[1]].Block.ShouldBe(new BlockIndex(1));
        ast[ifChildren[2]].Kind.ShouldBe(Avm1AstNodeKind.Block);
        ast[ifChildren[2]].Block.ShouldBe(new BlockIndex(2));

        result.GetStructuredAstDebugText().ShouldBe(string.Join(Environment.NewLine, [
            "root",
            "  if b0 actions=0..3 merge=3",
            "    unary !",
            "      variable",
            "        literal v0",
            "    block b1 actions=3..7",
            "      assign-variable",
            "        literal v2",
            "        literal v3",
            "    block b2 actions=7..10",
            "      assign-variable",
            "        literal v4",
            "        literal v5",
            "  block b3 actions=10..13",
            "    assign-variable",
            "      literal v6",
            "      literal v7",
            string.Empty
        ]));
    }

    [Fact]
    public void Builds_region_and_ast_for_if_without_else_shape()
    {
        var thenBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var mergeBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        };

        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0)
        };
        actions.AddRange(thenBody);
        actions.AddRange(mergeBody);

        var offsets = ComputeOffsets(actions);
        var ifIndex = 2;
        var mergeStart = ifIndex + 1 + thenBody.Length;

        SetBranch(actions, ifIndex, offsets[mergeStart]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var ifRegions = result.RegionAnalysis.IfRegions;

        ifRegions.Count.ShouldBe(1);
        ifRegions[0].Header.ShouldBe(new BlockIndex(0));
        ifRegions[0].FallThroughEntry.ShouldBe(new BlockIndex(1));
        ifRegions[0].BranchEntry.ShouldBe(new BlockIndex(2));
        ifRegions[0].Merge.ShouldBe(new BlockIndex(2));
        ifRegions[0].HasElse.ShouldBeFalse();

        var ast = result.StructuredAst;
        var rootChildren = ast.GetChildren(ast[ast.Root]);
        rootChildren.Count.ShouldBe(2);
        ast[rootChildren[0]].Kind.ShouldBe(Avm1AstNodeKind.If);
        ast[rootChildren[0]].Block.ShouldBe(new BlockIndex(0));
        ast[rootChildren[0]].Merge.ShouldBe(new BlockIndex(2));
        ast[rootChildren[1]].Kind.ShouldBe(Avm1AstNodeKind.Block);
        ast[rootChildren[1]].Block.ShouldBe(new BlockIndex(2));

        var ifChildren = ast.GetChildren(ast[rootChildren[0]]);
        ifChildren.Count.ShouldBe(2);
        ast[ifChildren[0]].Kind.ShouldBe(Avm1AstNodeKind.Unary);
        ast[ifChildren[1]].Kind.ShouldBe(Avm1AstNodeKind.Block);
        ast[ifChildren[1]].Block.ShouldBe(new BlockIndex(1));

        result.GetStructuredAstDebugText().ShouldBe(string.Join(Environment.NewLine, [
            "root",
            "  if b0 actions=0..3 merge=2",
            "    unary !",
            "      variable",
            "        literal v0",
            "    block b1 actions=3..6",
            "      assign-variable",
            "        literal v2",
            "        literal v3",
            "  block b2 actions=6..9",
            "    assign-variable",
            "      literal v4",
            "      literal v5",
            string.Empty
        ]));

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (!x)",
            "{",
            "    y = 1;",
            "}",
            "z = 3;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_if_preserves_side_effects_before_header_branch()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("before")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 5, offsets[9]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var rootChildren = result.StructuredAst.GetChildren(result.StructuredAst[result.StructuredAst.Root]);
        var composite = result.StructuredAst[rootChildren[0]];

        composite.Kind.ShouldBe(Avm1AstNodeKind.Block);
        result.StructuredAst.GetChildren(composite)
            .Select(index => result.StructuredAst[index].Kind)
            .ShouldBe([Avm1AstNodeKind.AssignVariable, Avm1AstNodeKind.If]);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "before = 1;",
            "if (!x)",
            "{",
            "    y = 2;",
            "}",
            "z = 3;",
            string.Empty
        ]));
    }

    [Fact]
    public void Builds_loop_analysis_for_while_shape()
    {
        var body = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0)
        };
        actions.AddRange(body);
        actions.Add(new ActionJump(0));

        var offsets = ComputeOffsets(actions);
        var ifIndex = 3;
        var jumpIndex = ifIndex + 1 + body.Length;
        var exit = jumpIndex + 1;

        SetBranch(actions, ifIndex, exit < offsets.Length ? offsets[exit] : offsets[^1] + Len(actions[^1]));
        SetBranch(actions, jumpIndex, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var loops = result.LoopAnalysis;
        var whileRegions = result.RegionAnalysis.WhileRegions;

        loops.BackEdges.Count.ShouldBe(1);
        loops.BackEdges[0].Tail.ShouldBe(new BlockIndex(1));
        loops.BackEdges[0].Header.ShouldBe(new BlockIndex(0));
        loops.Loops.Count.ShouldBe(1);
        loops.Loops[0].Header.ShouldBe(new BlockIndex(0));
        loops.Loops[0].Tail.ShouldBe(new BlockIndex(1));
        loops.Loops[0].Blocks.ShouldBe([new BlockIndex(0), new BlockIndex(1)]);
        loops.Loops[0].Exits.Count.ShouldBe(0);

        whileRegions.Count.ShouldBe(1);
        whileRegions[0].Header.ShouldBe(new BlockIndex(0));
        whileRegions[0].ConditionBlock.ShouldBe(new BlockIndex(0));
        whileRegions[0].BodyEntry.ShouldBe(new BlockIndex(1));
        whileRegions[0].Exit.ShouldBe(BlockIndex.Invalid);
        whileRegions[0].Blocks.ShouldBe([new BlockIndex(1)]);
        whileRegions[0].BodyBlocks.ShouldBe([new BlockIndex(1)]);

        var ast = result.StructuredAst;
        ast[ast.Root].Kind.ShouldBe(Avm1AstNodeKind.Root);
        var rootChildren = ast.GetChildren(ast[ast.Root]);
        rootChildren.Count.ShouldBe(1);
        ast[rootChildren[0]].Kind.ShouldBe(Avm1AstNodeKind.While);
        ast[rootChildren[0]].Block.ShouldBe(new BlockIndex(0));
        ast[rootChildren[0]].Merge.ShouldBe(BlockIndex.Invalid);

        var whileChildren = ast.GetChildren(ast[rootChildren[0]]);
        whileChildren.Count.ShouldBe(2);
        ast[whileChildren[0]].Kind.ShouldBe(Avm1AstNodeKind.Variable);
        ast[whileChildren[1]].Kind.ShouldBe(Avm1AstNodeKind.Block);
        ast[whileChildren[1]].Block.ShouldBe(new BlockIndex(1));

        result.GetStructuredAstDebugText().ShouldBe(string.Join(Environment.NewLine, [
            "root",
            "  while b0 actions=0..4 exit=end",
            "    variable",
            "      literal v0",
            "    block b1 actions=4..8",
            "      assign-variable",
            "        literal v3",
            "        literal v4",
            string.Empty
        ]));

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "while (x)",
            "{",
            "    y = 1;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_short_circuit_or_while_condition()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[7]);
        SetBranch(actions, index: 8, offsets[13]);
        SetBranch(actions, index: 12, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.RegionAnalysis.WhileRegions.ShouldHaveSingleItem();

        region.Header.ShouldBe(new BlockIndex(0));
        region.ConditionBlock.ShouldBe(new BlockIndex(2));
        region.BodyEntry.ShouldBe(new BlockIndex(3));
        region.Exit.ShouldBe(new BlockIndex(4));
        region.BodyBlocks.ShouldBe([new BlockIndex(3)]);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "while (a || b)",
            "{",
            "    y = 1;",
            "}",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_short_circuit_and_while_condition()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 4, offsets[8]);
        SetBranch(actions, index: 9, offsets[14]);
        SetBranch(actions, index: 13, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.RegionAnalysis.WhileRegions.ShouldHaveSingleItem()
            .ConditionBlock.ShouldBe(new BlockIndex(2));
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "while (a && b)",
            "{",
            "    y = 1;",
            "}",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_promotes_short_circuit_counter_loop_to_for()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("guard")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionIncrement(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 7, offsets[12]);
        SetBranch(actions, index: 13, offsets[22]);
        SetBranch(actions, index: 21, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc5_ = 0; guard && _loc5_ < 3; _loc5_++)",
            "{",
            "    y = _loc5_;",
            "}",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_preserves_if_else_merged_at_promoted_for_latch()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("guard")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("choice")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionIncrement(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 7, offsets[12]);
        SetBranch(actions, index: 13, offsets[30]);
        SetBranch(actions, index: 17, offsets[22]);
        SetBranch(actions, index: 21, offsets[25]);
        SetBranch(actions, index: 29, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc5_ = 0; guard && _loc5_ < 3; _loc5_++)",
            "{",
            "    if (choice)",
            "    {",
            "        y = 1;",
            "    }",
            "    else",
            "    {",
            "        y = 2;",
            "    }",
            "}",
            "z = 3;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_rejects_short_circuit_for_when_initializer_is_not_trailing()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("before")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("guard")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionIncrement(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 10, offsets[15]);
        SetBranch(actions, index: 16, offsets[25]);
        SetBranch(actions, index: 24, offsets[6]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.ShouldContain(node => node.Kind == Avm1AstNodeKind.While);
        result.StructuredAst.Nodes.ShouldNotContain(node => node.Kind == Avm1AstNodeKind.For);
        result.GetStructuredAs2Text().ShouldContain("before = 1;");
    }

    [Fact]
    public void Region_analysis_rejects_side_effecting_branch_as_short_circuit_while_condition()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[7]);
        SetBranch(actions, index: 6, offsets[7]);
        SetBranch(actions, index: 10, offsets[15]);
        SetBranch(actions, index: 14, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.RegionAnalysis.WhileRegions.ShouldBeEmpty();
        result.GetStructuredAs2Text().ShouldContain("x = 1;");
    }

    [Fact]
    public void Builds_region_and_emits_do_while_shape()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 5, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.LoopAnalysis.BackEdges.ShouldBe([new Avm1BackEdge(new BlockIndex(0), new BlockIndex(0))]);
        result.RegionAnalysis.WhileRegions.ShouldBeEmpty();
        result.RegionAnalysis.DoWhileRegions.Count.ShouldBe(1);

        var region = result.RegionAnalysis.DoWhileRegions[0];
        region.Header.ShouldBe(new BlockIndex(0));
        region.ConditionBlock.ShouldBe(new BlockIndex(0));
        region.Exit.ShouldBe(new BlockIndex(1));
        region.Blocks.ShouldBe([new BlockIndex(0)]);

        var ast = result.StructuredAst;
        var rootChildren = ast.GetChildren(ast[ast.Root]);
        ast[rootChildren[0]].Kind.ShouldBe(Avm1AstNodeKind.DoWhile);

        result.GetStructuredAstDebugText().ShouldBe(string.Join(Environment.NewLine, [
            "root",
            "  do-while b0 actions=0..6 exit=1",
            "    variable",
            "      literal v2",
            "    block b0 actions=0..6",
            "      assign-variable",
            "        literal v0",
            "        literal v1",
            "  block b1 actions=6..9",
            "    assign-variable",
            "      literal v4",
            "      literal v5",
            string.Empty
        ]));

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "do",
            "{",
            "    y = 1;",
            "} while (x);",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Do_while_region_does_not_absorb_its_preheader()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("i")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 8, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.LoopAnalysis.Loops[0].Blocks.ShouldBe([new BlockIndex(1)]);
        result.RegionAnalysis.DoWhileRegions[0].Blocks.ShouldBe([new BlockIndex(1)]);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "i = 0;",
            "do",
            "{",
            "    y = 1;",
            "} while (x);",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_short_circuit_do_while_condition()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 7, offsets[11]);
        SetBranch(actions, index: 11, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.RegionAnalysis.DoWhileRegions.ShouldHaveSingleItem();

        region.ConditionPrefixBlocks.ShouldBe([
            new BlockIndex(0),
            new BlockIndex(1)
        ]);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "do",
            "{",
            "    y = 1;",
            "} while (a && b);",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_omits_implicit_do_while_tail_continue()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("valid")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionJump(0),
            new ActionPush([PushValue.Boolean(true)]),
            new ActionIf(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[8]);
        SetBranch(actions, index: 7, offsets[9]);
        SetBranch(actions, index: 8, offsets[11]);
        SetBranch(actions, index: 10, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "do",
            "{",
            "    if (!valid)",
            "    {",
            "        break;",
            "    }",
            "    y = 1;",
            "} while (true);",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_guarded_continue_inside_do_while()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("skip")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[6]);
        SetBranch(actions, index: 8, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.RegionAnalysis.WhileRegions.ShouldBeEmpty();
        result.RegionAnalysis.DoWhileRegions.Count.ShouldBe(1);
        result.RegionAnalysis.DoWhileRegions[0].ConditionBlock.ShouldBe(new BlockIndex(2));

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "do",
            "{",
            "    if (skip)",
            "    {",
            "        continue;",
            "    }",
            "    y = 1;",
            "} while (x);",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_guarded_break_inside_do_while()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("done")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[9]);
        SetBranch(actions, index: 8, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.RegionAnalysis.DoWhileRegions.Count.ShouldBe(1);
        result.RegionAnalysis.DoWhileRegions[0].ConditionBlock.ShouldBe(new BlockIndex(1));

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "do",
            "{",
            "    if (done)",
            "    {",
            "        break;",
            "    }",
            "    y = 1;",
            "} while (x);",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_guarded_continue_inside_while()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("skip")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var loopHeader = 0;
        var headerIf = 3;
        var continueIf = 6;
        var tailJump = 10;
        var exitOffset = offsets[^1] + Len(actions[^1]);

        SetBranch(actions, headerIf, exitOffset);
        SetBranch(actions, continueIf, offsets[loopHeader]);
        SetBranch(actions, tailJump, offsets[loopHeader]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.LoopAnalysis.BackEdges.Count.ShouldBe(2);
        result.RegionAnalysis.WhileRegions.Count.ShouldBe(1);
        result.RegionAnalysis.WhileRegions[0].Blocks.ShouldBe([new BlockIndex(1), new BlockIndex(2)]);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "while (x)",
            "{",
            "    if (skip)",
            "    {",
            "        continue;",
            "    }",
            "    y = 1;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_guarded_break_inside_while()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("done")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        var loopHeader = 0;
        var headerIf = 3;
        var breakIf = 6;
        var tailJump = 10;
        var exitStart = 11;

        SetBranch(actions, headerIf, offsets[exitStart]);
        SetBranch(actions, breakIf, offsets[exitStart]);
        SetBranch(actions, tailJump, offsets[loopHeader]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.LoopAnalysis.BackEdges.Count.ShouldBe(1);
        result.RegionAnalysis.WhileRegions.Count.ShouldBe(1);
        result.RegionAnalysis.WhileRegions[0].Exit.ShouldBe(new BlockIndex(3));

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "while (x)",
            "{",
            "    if (done)",
            "    {",
            "        break;",
            "    }",
            "    y = 1;",
            "}",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_structures_nested_while_with_outer_continue()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("skip")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[19]);
        SetBranch(actions, index: 7, offsets[15]);
        SetBranch(actions, index: 10, offsets[0]);
        SetBranch(actions, index: 14, offsets[4]);
        SetBranch(actions, index: 18, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.RegionAnalysis.WhileRegions.Count.ShouldBe(2);
        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.While)
            .ShouldBe(2);
        var outerHeader = result.RegionAnalysis.WhileRegions
            .Single(region => region.Header.Value == 0)
            .Header;
        result.StructuredAst.Nodes.ShouldContain(node =>
            node.Kind == Avm1AstNodeKind.Continue &&
            node.Block == outerHeader);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "loop_b0:",
            "while (outer)",
            "{",
            "    while (inner)",
            "    {",
            "        if (skip)",
            "        {",
            "            continue loop_b0;",
            "        }",
            "        x = 1;",
            "    }",
            "    y = 2;",
            "}",
            "z = 3;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_structures_do_while_inside_while()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[14]);
        SetBranch(actions, index: 9, offsets[4]);
        SetBranch(actions, index: 13, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.RegionAnalysis.WhileRegions.Count.ShouldBe(1);
        result.RegionAnalysis.DoWhileRegions.Count.ShouldBe(1);
        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.While)
            .ShouldBe(1);
        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.DoWhile)
            .ShouldBe(1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "while (outer)",
            "{",
            "    do",
            "    {",
            "        x = 1;",
            "    } while (inner);",
            "    y = 2;",
            "}",
            "z = 3;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_promotes_register_counter_while_to_for()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var loopHeader = 3;
        var headerIf = 7;
        var tailJump = 16;
        var exitOffset = offsets[^1] + Len(actions[^1]);

        SetBranch(actions, headerIf, exitOffset);
        SetBranch(actions, tailJump, offsets[loopHeader]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst[result.StructuredAst.GetChildren(result.StructuredAst[result.StructuredAst.Root])[0]].Kind
            .ShouldBe(Avm1AstNodeKind.For);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc5_ = 0; _loc5_ < 3; _loc5_ += 1)",
            "{",
            "    y = _loc5_;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_promotes_counter_nested_in_member_condition_to_for()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("items")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionGetMember(),
            new ActionPush([PushValue.Undefined()]),
            new ActionEquals2(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionIncrement(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var exitOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 9, exitOffset);
        SetBranch(actions, index: 17, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.For)
            .ShouldBe(1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc5_ = 0; items[_loc5_] != undefined; _loc5_++)",
            "{",
            "    y = _loc5_;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_promotes_loop_carried_object_condition_to_for()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("start")]),
            new ActionGetVariable(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.Register(5)]),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.String("_parent")]),
            new ActionGetMember(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var exitOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 6, exitOffset);
        SetBranch(actions, index: 15, offsets[4]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc5_ = start; _loc5_; _loc5_ = _loc5_._parent)",
            "{",
            "    y = _loc5_;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_rejects_nested_counter_for_when_initializer_is_not_trailing()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("before")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("items")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionGetMember(),
            new ActionPush([PushValue.Undefined()]),
            new ActionEquals2(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionIncrement(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var exitOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 12, exitOffset);
        SetBranch(actions, index: 20, offsets[6]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.ShouldContain(node => node.Kind == Avm1AstNodeKind.While);
        result.StructuredAst.Nodes.ShouldNotContain(node => node.Kind == Avm1AstNodeKind.For);
        result.GetStructuredAs2Text().ShouldContain("before = 1;");
    }

    [Fact]
    public void Structured_as2_emitter_recovers_cs6_increment_loop_with_mixed_preheader()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("before")]),
            new ActionPush([PushValue.Integer(7)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(5)]),
            new ActionIncrement(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var exitOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 10, exitOffset);
        SetBranch(actions, index: 18, offsets[6]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.Instructions.Select(instruction => instruction.Op)
            .ShouldContain(Avm1StackIrOp.Increment);
        result.TacIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1TacOp.Unary &&
            instruction.Opcode == ActionOpcode.Increment);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "before = 7;",
            "for (var _loc5_ = 0; _loc5_ < 3; _loc5_++)",
            "{",
            "    y = _loc5_;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_tracks_loop_counter_declaration_per_emission()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.Register(2)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("remove")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Register(2)]),
            new ActionDecrement(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.Register(2)]),
            new ActionIncrement(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var exitOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 7, exitOffset);
        SetBranch(actions, index: 10, offsets[15]);
        SetBranch(actions, index: 19, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var expected = string.Join(Environment.NewLine, [
            "for (var _loc2_ = 0; _loc2_ < 3; _loc2_++)",
            "{",
            "    if (!remove)",
            "    {",
            "        _loc2_--;",
            "    }",
            "}",
            string.Empty
        ]);

        result.GetStructuredAs2Text().ShouldBe(expected);
        result.GetStructuredAs2Text().ShouldBe(expected);
    }

    [Fact]
    public void Structured_as2_emitter_repeats_local_declarations_idempotently()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionStoreRegister(3),
            new ActionPop(),
            new ActionPush([PushValue.String("first")]),
            new ActionPush([PushValue.Register(3)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("second")]),
            new ActionPush([PushValue.Register(3)]),
            new ActionSetVariable()
        ], swfVersion: 6);

        var firstEmission = result.GetStructuredAs2Text();

        firstEmission.ShouldContain("var _loc3_ = probe();");
        result.GetStructuredAs2Text().ShouldBe(firstEmission);
    }

    [Fact]
    public void Structured_as2_emitter_recovers_postfix_register_updates_consumed_by_indexers()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("start")]),
            new ActionGetVariable(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.String("item")]),
            new ActionPush([PushValue.String("arr")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Register(1), PushValue.Register(1)]),
            new ActionIncrement(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionGetMember(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("next")]),
            new ActionPush([PushValue.Register(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("last")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("previous")]),
            new ActionPush([PushValue.String("arr")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Register(2), PushValue.Register(2)]),
            new ActionDecrement(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionGetMember(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("remaining")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.Postfix)
            .ShouldBe(2);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc1_ = start;",
            "item = arr[_loc1_++];",
            "next = _loc1_;",
            "var _loc2_ = last;",
            "previous = arr[_loc2_--];",
            "remaining = _loc2_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_inlines_immediate_call_target_before_postfix_index()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("start")]),
            new ActionGetVariable(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.String("item")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("getArray")]),
            new ActionCallFunction(),
            new ActionPush([PushValue.Register(1), PushValue.Register(1)]),
            new ActionIncrement(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionGetMember(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("next")]),
            new ActionPush([PushValue.Register(1)]),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc1_ = start;",
            "item = getArray()[_loc1_++];",
            "next = _loc1_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_keeps_postfix_target_before_intervening_call()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("start")]),
            new ActionGetVariable(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.String("item")]),
            new ActionPush([PushValue.String("arr")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.Register(1), PushValue.Register(1)]),
            new ActionIncrement(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionGetMember(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("next")]),
            new ActionPush([PushValue.Register(1)]),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc1_ = start;",
            "var v4 = arr;",
            "probe();",
            "item = v4[_loc1_++];",
            "next = _loc1_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_keeps_standalone_postfix_register_update_live()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("start")]),
            new ActionGetVariable(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.Register(1), PushValue.Register(1)]),
            new ActionIncrement(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPop(),
            new ActionPush([PushValue.String("next")]),
            new ActionPush([PushValue.Register(1)]),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.Count(node =>
            node.Kind is Avm1AstNodeKind.Postfix).ShouldBe(0);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc1_ = start;",
            "_loc1_ = _loc1_ + 1;",
            "next = _loc1_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Value_analysis_folds_avm1_increment_and_decrement()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("incremented")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionIncrement(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("decremented")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionDecrement(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.Instructions.Count(instruction =>
            instruction.Op is Avm1StackIrOp.Increment or Avm1StackIrOp.Decrement).ShouldBe(2);
        result.TacIr.Instructions.Count(instruction =>
            instruction.Op is Avm1TacOp.Unary).ShouldBe(2);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "incremented = 5;",
            "decremented = 3;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_promotes_variable_counter_while_to_for()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("i")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("i")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(3)]),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.String("i")]),
            new ActionGetVariable(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("i")]),
            new ActionPush([PushValue.String("i")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionSetVariable(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var loopHeader = 3;
        var headerIf = 8;
        var tailJump = 19;
        var exitOffset = offsets[^1] + Len(actions[^1]);

        SetBranch(actions, headerIf, exitOffset);
        SetBranch(actions, tailJump, offsets[loopHeader]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst[result.StructuredAst.GetChildren(result.StructuredAst[result.StructuredAst.Root])[0]].Kind
            .ShouldBe(Avm1AstNodeKind.For);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (i = 0; i < 3; i += 1)",
            "{",
            "    y = i;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_promotes_enumeration_loop_to_for_in()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("arr")]),
            new ActionGetVariable(),
            new ActionEnumerate2(),
            new ActionStoreRegister(1),
            new ActionPush([PushValue.Null()]),
            new ActionEquals2(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        var loopHeader = 3;
        var headerIf = 6;
        var tailJump = 10;
        var exitStart = 11;

        SetBranch(actions, headerIf, offsets[exitStart]);
        SetBranch(actions, tailJump, offsets[loopHeader]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.Instructions.Select(i => i.Op).ShouldContain(Avm1StackIrOp.Enumerate);
        result.TacIr.Instructions.Select(i => i.Op).ShouldContain(Avm1TacOp.Enumerate);
        result.StackDepthAnalysis.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Message.StartsWith("Inconsistent stack depth", StringComparison.Ordinal));

        var rootChildren = result.StructuredAst.GetChildren(result.StructuredAst[result.StructuredAst.Root]);
        result.StructuredAst[rootChildren[0]].Kind.ShouldBe(Avm1AstNodeKind.ForIn);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc1_ in arr)",
            "{",
            "    y = _loc1_;",
            "}",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_for_in_nested_inside_if()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("enabled")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("source")]),
            new ActionGetVariable(),
            new ActionEnumerate2(),
            new ActionStoreRegister(1),
            new ActionPush([PushValue.Null()]),
            new ActionEquals2(),
            new ActionIf(0),
            new ActionPush([PushValue.String("target")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Register(1)]),
            new ActionPush([PushValue.String("source")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Register(1)]),
            new ActionGetMember(),
            new ActionSetMember(),
            new ActionJump(0),
            new ActionPush([PushValue.String("done")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[20]);
        SetBranch(actions, index: 10, offsets[20]);
        SetBranch(actions, index: 19, offsets[7]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (enabled)",
            "{",
            "    for (var _loc1_ in source)",
            "    {",
            "        target[_loc1_] = source[_loc1_];",
            "    }",
            "}",
            "done = 1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_guarded_break_arm_outside_natural_loop()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.Register(1)]),
            new ActionPush([PushValue.String("n")]),
            new ActionGetVariable(),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("match")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("found")]),
            new ActionPush([PushValue.Register(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.Register(1)]),
            new ActionIncrement(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionJump(0),
            new ActionPush([PushValue.String("done")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 8, offsets[22]);
        SetBranch(actions, index: 12, offsets[17]);
        SetBranch(actions, index: 16, offsets[22]);
        SetBranch(actions, index: 21, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc1_ = 0; _loc1_ < n; _loc1_++)",
            "{",
            "    if (match)",
            "    {",
            "        found = _loc1_;",
            "        break;",
            "    }",
            "}",
            "done = 1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_latch_continue_and_terminal_return_arms()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.Register(1)]),
            new ActionPush([PushValue.String("n")]),
            new ActionGetVariable(),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("hidden")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionJump(0),
            new ActionPush([PushValue.Register(2)]),
            new ActionPush([PushValue.String("index")]),
            new ActionGetVariable(),
            new ActionEquals2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Register(1)]),
            new ActionReturn(),
            new ActionPush([PushValue.Register(2)]),
            new ActionIncrement(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.Register(1)]),
            new ActionIncrement(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(-1)]),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 11, offsets[34]);
        SetBranch(actions, index: 15, offsets[17]);
        SetBranch(actions, index: 16, offsets[29]);
        SetBranch(actions, index: 22, offsets[25]);
        SetBranch(actions, index: 33, offsets[6]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc2_ = 0;",
            "for (var _loc1_ = 0; _loc1_ < n; _loc1_++)",
            "{",
            "    if (hidden)",
            "    {",
            "        continue;",
            "    }",
            "    if (_loc2_ == index)",
            "    {",
            "        return _loc1_;",
            "    }",
            "    _loc2_ = _loc2_ + 1;",
            "}",
            "return -1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_normalizes_nested_terminal_arm_to_guard_clause()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("n")]),
            new ActionGetVariable(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.Register(1), PushValue.Register(1)]),
            new ActionDecrement(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("match")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("clear")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("broadcast")]),
            new ActionPush([PushValue.Undefined()]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Boolean(true)]),
            new ActionReturn(),
            new ActionJump(0),
            new ActionPush([PushValue.Boolean(false)]),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 9, offsets[24]);
        SetBranch(actions, index: 13, offsets[23]);
        SetBranch(actions, index: 17, offsets[21]);
        SetBranch(actions, index: 23, offsets[4]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc1_ = n;",
            "while (_loc1_--)",
            "{",
            "    if (!match)",
            "    {",
            "        continue;",
            "    }",
            "    if (clear)",
            "    {",
            "        broadcast = undefined;",
            "    }",
            "    return true;",
            "}",
            "return false;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_suppresses_for_in_return_stack_cleanup()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("arr")]),
            new ActionGetVariable(),
            new ActionEnumerate2(),
            new ActionStoreRegister(1),
            new ActionPush([PushValue.Null()]),
            new ActionEquals2(),
            new ActionIf(0),
            new ActionPush([PushValue.String("match")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Null()]),
            new ActionEquals2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Register(1)]),
            new ActionReturn(),
            new ActionJump(0),
            new ActionPush([PushValue.Null()]),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 6, offsets[18]);
        SetBranch(actions, index: 10, offsets[17]);
        SetBranch(actions, index: 14, offsets[11]);
        SetBranch(actions, index: 17, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Message.StartsWith("Stack underflow", StringComparison.Ordinal));
        result.StackDepthAnalysis.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Message.StartsWith("Stack underflow", StringComparison.Ordinal));
        result.StackDepthAnalysis.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Message.StartsWith("Inconsistent stack depth", StringComparison.Ordinal));
        result.StructuredAst.Nodes.ShouldNotContain(node =>
            node.Kind == Avm1AstNodeKind.DoWhile);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc1_ in arr)",
            "{",
            "    if (!match)",
            "    {",
            "        continue;",
            "    }",
            "    return _loc1_;",
            "}",
            "return null;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_suppresses_for_in_latch_transport_phi()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("arr")]),
            new ActionGetVariable(),
            new ActionEnumerate2(),
            new ActionStoreRegister(1),
            new ActionPush([PushValue.Null()]),
            new ActionEquals2(),
            new ActionIf(0),
            new ActionPush([PushValue.String("match")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Register(1)]),
            new ActionReturn(),
            new ActionJump(0),
            new ActionJump(0),
            new ActionJump(0),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 6, offsets[16]);
        SetBranch(actions, index: 10, offsets[14]);
        SetBranch(actions, index: 13, offsets[15]);
        SetBranch(actions, index: 14, offsets[15]);
        SetBranch(actions, index: 15, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc1_ in arr)",
            "{",
            "    if (match)",
            "    {",
            "        return _loc1_;",
            "    }",
            "}",
            "return;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_includes_break_only_arm_in_outer_loop_region()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("skip")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("body")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("marker")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionJump(0),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[20]);
        SetBranch(actions, index: 6, offsets[19]);
        SetBranch(actions, index: 10, offsets[15]);
        SetBranch(actions, index: 14, offsets[7]);
        SetBranch(actions, index: 18, offsets[20]);
        SetBranch(actions, index: 19, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "while (outer)",
            "{",
            "    if (skip)",
            "    {",
            "        continue;",
            "    }",
            "    while (inner)",
            "    {",
            "        body = 1;",
            "    }",
            "    marker = 3;",
            "    break;",
            "}",
            "after = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_break_after_nested_for_in_cleanup()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionEnumerate2(),
            new ActionStoreRegister(1),
            new ActionPush([PushValue.Null()]),
            new ActionEquals2(),
            new ActionIf(0),
            new ActionPush([PushValue.String("skip")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionJump(0),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionEnumerate2(),
            new ActionStoreRegister(2),
            new ActionPush([PushValue.Null()]),
            new ActionEquals2(),
            new ActionIf(0),
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.Null()]),
            new ActionEquals2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("done")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 6, offsets[27]);
        SetBranch(actions, index: 10, offsets[12]);
        SetBranch(actions, index: 11, offsets[3]);
        SetBranch(actions, index: 18, offsets[23]);
        SetBranch(actions, index: 22, offsets[15]);
        SetBranch(actions, index: 26, offsets[23]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc1_ in outer)",
            "{",
            "    if (skip)",
            "    {",
            "        continue;",
            "    }",
            "    for (var _loc2_ in inner)",
            "    {",
            "        result = _loc2_;",
            "    }",
            "    break;",
            "}",
            "done = 1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_distinguishes_explicit_continue_from_normal_latch_fallthrough()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.Register(1)]),
            new ActionPush([PushValue.String("n")]),
            new ActionGetVariable(),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("skip")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionJump(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(1)]),
            new ActionIncrement(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var exitOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 8, exitOffset);
        SetBranch(actions, index: 12, offsets[14]);
        SetBranch(actions, index: 13, offsets[20]);
        SetBranch(actions, index: 24, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc1_ = 0; _loc1_ < n; _loc1_++)",
            "{",
            "    if (skip)",
            "    {",
            "        continue;",
            "    }",
            "    y = 1;",
            "    z = 2;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prefers_nullish_continue_guard_before_single_statement()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.Register(1)]),
            new ActionPush([PushValue.String("n")]),
            new ActionGetVariable(),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("value")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Undefined()]),
            new ActionEquals2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionJump(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(1)]),
            new ActionIncrement(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionJump(0)
        };

        var offsets = ComputeOffsets(actions);
        var exitOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 8, exitOffset);
        SetBranch(actions, index: 14, offsets[16]);
        SetBranch(actions, index: 15, offsets[19]);
        SetBranch(actions, index: 23, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc1_ = 0; _loc1_ < n; _loc1_++)",
            "{",
            "    if (value == undefined)",
            "    {",
            "        continue;",
            "    }",
            "    y = 1;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_resolves_action_enumerate_variable_name()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("arr")]),
            new ActionEnumerate(),
            new ActionStoreRegister(1),
            new ActionPush([PushValue.Null()]),
            new ActionEquals2(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 5, offsets[10]);
        SetBranch(actions, index: 9, offsets[2]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc1_ in arr)",
            "{",
            "    y = _loc1_;",
            "}",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_preserves_guarded_continue_in_for_in()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("arr")]),
            new ActionGetVariable(),
            new ActionEnumerate2(),
            new ActionStoreRegister(1),
            new ActionPush([PushValue.Null()]),
            new ActionEquals2(),
            new ActionIf(0),
            new ActionPush([PushValue.String("skip")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Register(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 6, offsets[14]);
        SetBranch(actions, index: 9, offsets[3]);
        SetBranch(actions, index: 13, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.LoopAnalysis.BackEdges.Count.ShouldBe(2);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "for (var _loc1_ in arr)",
            "{",
            "    if (skip)",
            "    {",
            "        continue;",
            "    }",
            "    y = _loc1_;",
            "}",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Stack_ir_records_underflow_without_throwing()
    {
        var result = Avm1Decompiler.DecompileMethod([new ActionPop()], swfVersion: 6);

        result.StackIr.Diagnostics.Count.ShouldBe(1);
        result.StackIr.Diagnostics[0].Severity.ShouldBe(Avm1DiagnosticSeverity.Warning);
        result.StackIr.Values[0].Kind.ShouldBe(Avm1StackValueKind.Unknown);
        result.StackIr.Instructions[0].Op.ShouldBe(Avm1StackIrOp.Pop);
    }

    [Fact]
    public void Stack_depth_analysis_tracks_balanced_straight_line_code()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("x")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackDepthAnalysis.Diagnostics.Count.ShouldBe(0);
        result.StackDepthAnalysis.HasKnownEntry[0].ShouldBeTrue();
        result.StackDepthAnalysis.EntryDepths[0].ShouldBe(0);
        result.StackDepthAnalysis.ExitDepths[0].ShouldBe(0);
    }

    [Fact]
    public void Stack_depth_analysis_resolves_literal_counts_for_variadic_actions()
    {
        Avm1Action[][] cases =
        [
            [
                new ActionPush([
                    PushValue.String("right"),
                    PushValue.String("left"),
                    PushValue.Integer(2),
                    PushValue.String("fn")]),
                new ActionCallFunction(),
                new ActionPop()
            ],
            [
                new ActionPush([
                    PushValue.String("right"),
                    PushValue.String("left"),
                    PushValue.Integer(2),
                    PushValue.String("object"),
                    PushValue.String("method")]),
                new ActionCallMethod(),
                new ActionPop()
            ],
            [
                new ActionPush([
                    PushValue.String("second"),
                    PushValue.String("first"),
                    PushValue.Integer(2)]),
                new ActionInitArray(),
                new ActionPop()
            ],
            [
                new ActionPush([
                    PushValue.String("second"),
                    PushValue.Integer(2),
                    PushValue.String("first"),
                    PushValue.Integer(1),
                    PushValue.Integer(2)]),
                new ActionInitObject(),
                new ActionPop()
            ],
            [
                new ActionPush([
                    PushValue.String("IFirst"),
                    PushValue.String("ISecond"),
                    PushValue.Integer(2),
                    PushValue.String("Class")]),
                new ActionImplementsOp()
            ],
            [
                new ActionPush([
                    PushValue.Boolean(false),
                    PushValue.Boolean(false),
                    PushValue.String("target")]),
                new ActionStartDrag()
            ]
        ];

        foreach (var actions in cases)
        {
            var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);

            result.StackDepthAnalysis.Diagnostics.ShouldBeEmpty();
            result.StackDepthAnalysis.ExitDepths[0].ShouldBe(0);
        }
    }

    [Fact]
    public void Stack_depth_analysis_aggregates_unresolved_dynamic_effects_by_opcode()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.Register(1), PushValue.String("first")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.Register(1), PushValue.String("second")]),
            new ActionCallFunction(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);

        var diagnostic = result.StackDepthAnalysis.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(Avm1DiagnosticSeverity.Info);
        diagnostic.Message.ShouldContain(nameof(ActionOpcode.CallFunction));
    }

    [Fact]
    public void Stack_depth_analysis_only_pushes_anonymous_function_literals()
    {
        var named = Avm1Decompiler.DecompileMethod(
            [new ActionDefineFunction("named", [], ReadOnlyMemory<byte>.Empty)],
            swfVersion: 7);
        var anonymous = Avm1Decompiler.DecompileMethod(
            [new ActionDefineFunction(
                string.Empty,
                [],
                ReadOnlyMemory<byte>.Empty), new ActionPop()],
            swfVersion: 7);

        named.StackDepthAnalysis.ExitDepths[0].ShouldBe(0);
        anonymous.StackDepthAnalysis.ExitDepths[0].ShouldBe(0);
        named.StackDepthAnalysis.Diagnostics.ShouldBeEmpty();
        anonymous.StackDepthAnalysis.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Stack_depth_analysis_reports_underflow()
    {
        var result = Avm1Decompiler.DecompileMethod([new ActionPop()], swfVersion: 6);

        result.StackDepthAnalysis.Diagnostics.Count.ShouldBe(1);
        result.StackDepthAnalysis.Diagnostics[0].Severity.ShouldBe(Avm1DiagnosticSeverity.Warning);
        result.StackDepthAnalysis.ExitDepths[0].ShouldBe(0);
    }

    [Fact]
    public void Stack_analysis_accepts_conditional_assignment_cleanup_pop()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([
                PushValue.String("field"),
                PushValue.Integer(1)]),
            new ActionSetMember(),
            new ActionPop(),
            new ActionPush([
                PushValue.String("after"),
                PushValue.Integer(2)]),
            new ActionSetVariable()
        };
        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[8]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.Diagnostics.ShouldBeEmpty();
        result.StackDepthAnalysis.Diagnostics.ShouldBeEmpty();
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (condition)",
            "{",
            "    obj.field = 1;",
            "}",
            "after = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Stack_analysis_accepts_conditional_call_result_cleanup_pop()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([
                PushValue.Integer(0),
                PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([
                PushValue.String("after"),
                PushValue.Integer(2)]),
            new ActionSetVariable()
        };
        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[6]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.Diagnostics.ShouldBeEmpty();
        result.StackDepthAnalysis.Diagnostics.ShouldBeEmpty();
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (condition)",
            "{",
            "    probe();",
            "}",
            "after = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Metadata_actions_lower_to_no_op_rows_instead_of_unknown_actions()
    {
        var actions = new Avm1Action[]
        {
            new ActionConstantPool(["name"]),
            new ActionEnd()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);

        result.StackIr.Instructions.Select(instruction => instruction.Op).ShouldBe([
            Avm1StackIrOp.NoOp,
            Avm1StackIrOp.NoOp
        ]);
        result.TacIr.Instructions.Select(instruction => instruction.Op).ShouldBe([
            Avm1TacOp.NoOp,
            Avm1TacOp.NoOp
        ]);
        result.StackIr.Diagnostics.ShouldBeEmpty();
        result.StackDepthAnalysis.ExitDepths[0].ShouldBe(0);
        result.GetStructuredAs2Text().ShouldBeEmpty();
    }

    [Fact]
    public void Structured_as2_emitter_lowers_local_declarations_and_trace()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("value")]),
            new ActionPush([PushValue.Integer(7)]),
            new ActionDefineLocal(),
            new ActionPush([PushValue.String("empty")]),
            new ActionDefineLocal2(),
            new ActionPush([PushValue.String("message")]),
            new ActionGetVariable(),
            new ActionTrace()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);

        result.StackIr.Instructions.ShouldNotContain(instruction =>
            instruction.Op == Avm1StackIrOp.UnknownAction);
        result.TacIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1TacOp.DefineLocal);
        result.TacIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1TacOp.Trace);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var value = 7;",
            "var empty;",
            "trace(message);",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_lowers_remaining_skyui_expression_opcodes()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("clock")]),
            new ActionGetTime(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("path")]),
            new ActionPush([PushValue.String("clip")]),
            new ActionGetVariable(),
            new ActionTargetPath(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("number")]),
            new ActionPush([PushValue.String("raw")]),
            new ActionGetVariable(),
            new ActionToNumber(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("text")]),
            new ActionPush([PushValue.String("raw")]),
            new ActionGetVariable(),
            new ActionToString(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("kind")]),
            new ActionPush([PushValue.String("raw")]),
            new ActionGetVariable(),
            new ActionTypeOf(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("matches")]),
            new ActionPush([PushValue.String("raw")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("MovieClip")]),
            new ActionGetVariable(),
            new ActionInstanceOf(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("deleted")]),
            new ActionPush([PushValue.String("target")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionDelete(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("target")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("other")]),
            new ActionDelete(),
            new ActionPop(),
            new ActionPush([PushValue.String("temporary")]),
            new ActionDelete2(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);

        result.StackIr.Instructions.ShouldNotContain(instruction =>
            instruction.Op == Avm1StackIrOp.UnknownAction);
        result.ValueAnalysis[result.TacIr.Instructions.Single(instruction =>
            instruction.Op == Avm1TacOp.GetTime).Result].Type.ShouldBe(Avm1InferredType.Number);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "clock = getTimer();",
            "path = targetPath(clip);",
            "number = Number(raw);",
            "text = String(raw);",
            "kind = typeof raw;",
            "matches = raw instanceof MovieClip;",
            "deleted = delete target.field;",
            "delete target.other;",
            "delete temporary;",
            string.Empty
        ]));
    }

    [Fact]
    public void Class_metadata_analysis_recovers_qualified_base_class_from_extends()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("_global")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("Child")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("skyui")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("components")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("list")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("BasicList")]),
            new ActionGetMember(),
            new ActionStoreRegister(3),
            new ActionPop(),
            new ActionPush([PushValue.Register(3)]),
            new ActionExtends()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);
        var metadata = Avm1ClassMetadataAnalysis.Build(result.Core, "Child");
        var extends = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.Extends);
        var baseClassLoad = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.LoadRegister && instruction.IntOperand == 3);

        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.Extends);
        extends.Operand0.IsValid.ShouldBeTrue();
        extends.Operand1.IsValid.ShouldBeTrue();
        result.ValueOrigins[baseClassLoad.Result].ShouldBe(
            Avm1ValueOrigin.StaticMember("skyui.components.list", "BasicList"));
        metadata.BaseClassName.ShouldBe("skyui.components.list.BasicList");
        metadata.InterfaceNames.ShouldBeEmpty();
        result.StackDepthAnalysis.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Class_metadata_analysis_recovers_interfaces_from_implements_op()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("skyui")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("components")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("list")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("IListProcessor")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("other")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("ISecond")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.String("_global")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("Child")]),
            new ActionGetMember(),
            new ActionImplementsOp()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);
        var metadata = Avm1ClassMetadataAnalysis.Build(result.Core, "Child");
        var implements = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.Implements);

        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.Implements);
        implements.OperandCount.ShouldBe(2);
        implements.Operand2.IsValid.ShouldBeTrue();
        metadata.BaseClassName.ShouldBeNull();
        metadata.InterfaceNames.ShouldBe([
            "skyui.components.list.IListProcessor",
            "other.ISecond"
        ]);
        result.StackIr.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == Avm1DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Tac_ir_preserves_register_loads_and_stores()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionAdd2(),
            new ActionStoreRegister(5),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.Values[1].Kind.ShouldBe(Avm1StackValueKind.Register);
        result.StackIr.Values[1].IntValue.ShouldBe(5);

        result.TacIr.Instructions.Select(i => i.Op).ShouldBe([
            Avm1TacOp.LoadConstant,
            Avm1TacOp.StoreRegister,
            Avm1TacOp.Pop,
            Avm1TacOp.LoadRegister,
            Avm1TacOp.LoadConstant,
            Avm1TacOp.Binary,
            Avm1TacOp.StoreRegister,
            Avm1TacOp.Pop
        ]);

        result.TacIr.Instructions[1].IntOperand.ShouldBe(5);
        result.TacIr.Instructions[3].IntOperand.ShouldBe(5);
        result.TacIr.Instructions[6].IntOperand.ShouldBe(5);
    }

    [Fact]
    public void Register_ssa_versions_linear_register_accesses()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionAdd2(),
            new ActionStoreRegister(5),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var accesses = result.RegisterSsa.Accesses;

        accesses.Count.ShouldBe(3);
        accesses[0].Kind.ShouldBe(Avm1RegisterAccessKind.Write);
        accesses[0].Register.ShouldBe(5);
        accesses[0].Version.ShouldBe(1);
        accesses[1].Kind.ShouldBe(Avm1RegisterAccessKind.Read);
        accesses[1].Register.ShouldBe(5);
        accesses[1].Version.ShouldBe(1);
        accesses[2].Kind.ShouldBe(Avm1RegisterAccessKind.Write);
        accesses[2].Register.ShouldBe(5);
        accesses[2].Version.ShouldBe(2);
        result.RegisterSsa.VersionCountByRegister[5].ShouldBe(2);
    }

    [Fact]
    public void Register_ssa_inserts_phi_for_if_else_register_merge()
    {
        var thenBody = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop()
        };

        var elseBody = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(2)]),
            new ActionStoreRegister(5),
            new ActionPop()
        };

        var mergeBody = new Avm1Action[]
        {
            new ActionPush([PushValue.Register(5)]),
            new ActionPop()
        };

        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0)
        };
        actions.AddRange(thenBody);
        actions.Add(new ActionJump(0));
        actions.AddRange(elseBody);
        actions.AddRange(mergeBody);

        var offsets = ComputeOffsets(actions);
        var ifIndex = 2;
        var jumpIndex = ifIndex + 1 + thenBody.Length;
        var elseStart = jumpIndex + 1;
        var mergeStart = elseStart + elseBody.Length;

        SetBranch(actions, ifIndex, offsets[elseStart]);
        SetBranch(actions, jumpIndex, offsets[mergeStart]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var ssa = result.RegisterSsa;

        ssa.PhiNodes.Count.ShouldBe(1);
        ssa.PhiNodes[0].Block.ShouldBe(new BlockIndex(3));
        ssa.PhiNodes[0].Register.ShouldBe(5);
        ssa.PhiNodes[0].Version.ShouldBe(3);
        ssa.PhiNodes[0].Predecessors.ShouldBe([new BlockIndex(1), new BlockIndex(2)]);
        ssa.PhiNodes[0].IncomingVersions.ShouldBe([1, 2]);

        ssa.Accesses.Count.ShouldBe(3);
        ssa.Accesses[0].Kind.ShouldBe(Avm1RegisterAccessKind.Write);
        ssa.Accesses[0].Version.ShouldBe(1);
        ssa.Accesses[1].Kind.ShouldBe(Avm1RegisterAccessKind.Write);
        ssa.Accesses[1].Version.ShouldBe(2);
        ssa.Accesses[2].Kind.ShouldBe(Avm1RegisterAccessKind.Read);
        ssa.Accesses[2].Version.ShouldBe(3);
        ssa.VersionCountByRegister[5].ShouldBe(3);
    }

    [Fact]
    public void Register_ssa_inserts_phi_for_loop_header_register_merge()
    {
        var body = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop()
        };

        var exitBody = new Avm1Action[]
        {
            new ActionPush([PushValue.Register(5)]),
            new ActionPop()
        };

        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0)
        };
        actions.AddRange(body);
        actions.Add(new ActionJump(0));
        actions.AddRange(exitBody);

        var offsets = ComputeOffsets(actions);
        var conditionStart = 3;
        var ifIndex = 6;
        var jumpIndex = ifIndex + 1 + body.Length;
        var exitStart = jumpIndex + 1;

        SetBranch(actions, ifIndex, offsets[exitStart]);
        SetBranch(actions, jumpIndex, offsets[conditionStart]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var ssa = result.RegisterSsa;

        ssa.PhiNodes.Count.ShouldBe(1);
        ssa.PhiNodes[0].Block.ShouldBe(new BlockIndex(1));
        ssa.PhiNodes[0].Register.ShouldBe(5);
        ssa.PhiNodes[0].Version.ShouldBe(3);
        ssa.PhiNodes[0].Predecessors.ShouldBe([new BlockIndex(0), new BlockIndex(2)]);
        ssa.PhiNodes[0].IncomingVersions.ShouldBe([1, 2]);

        ssa.Accesses.Count.ShouldBe(3);
        ssa.Accesses[0].Kind.ShouldBe(Avm1RegisterAccessKind.Write);
        ssa.Accesses[0].Version.ShouldBe(1);
        ssa.Accesses[1].Kind.ShouldBe(Avm1RegisterAccessKind.Write);
        ssa.Accesses[1].Version.ShouldBe(2);
        ssa.Accesses[2].Kind.ShouldBe(Avm1RegisterAccessKind.Read);
        ssa.Accesses[2].Version.ShouldBe(3);
        ssa.VersionCountByRegister[5].ShouldBe(3);
    }

    [Fact]
    public void Value_analysis_folds_numeric_constants_and_propagates_register_values()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(40)]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionAdd2(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var binaryResult = result.TacIr.Instructions[2].Result;
        var registerLoad = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.LoadRegister).Result;

        result.ValueAnalysis[binaryResult].Type.ShouldBe(Avm1InferredType.Integer);
        result.ValueAnalysis[binaryResult].ConstantKind.ShouldBe(Avm1ConstantKind.Integer);
        result.ValueAnalysis[binaryResult].IntegerValue.ShouldBe(42);
        result.ValueAnalysis[registerLoad].Type.ShouldBe(Avm1InferredType.Integer);
        result.ValueAnalysis[registerLoad].ConstantKind.ShouldBe(Avm1ConstantKind.Integer);
        result.ValueAnalysis[registerLoad].IntegerValue.ShouldBe(42);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = 42;",
            string.Empty
        ]));
    }

    [Fact]
    public void Value_analysis_preserves_ordinals_in_mixed_multi_value_push()
    {
        var actions = new Avm1Action[]
        {
            new ActionConstantPool(["memberName"]),
            new ActionPush([
                PushValue.Register(1),
                PushValue.Integer(7),
                PushValue.Constant8(0)
            ]),
            new ActionPop(),
            new ActionPop(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var pushedValues = result.TacIr.Instructions
            .Where(instruction => instruction.Action == new ActionIndex(1))
            .ToArray();

        pushedValues.Select(instruction => instruction.Op).ShouldBe([
            Avm1TacOp.LoadRegister,
            Avm1TacOp.LoadConstant,
            Avm1TacOp.LoadConstant
        ]);
        result.ValueAnalysis[pushedValues[1].Result].IntegerValue.ShouldBe(7);
        result.ValueAnalysis[pushedValues[2].Result].ConstantKind.ShouldBe(Avm1ConstantKind.String);
        result.ValueAnalysis[pushedValues[2].Result].StringValue.ShouldBe("memberName");
    }

    [Fact]
    public void Constant_pool_analysis_tracks_replacement_at_each_action()
    {
        var actions = new Avm1Action[]
        {
            new ActionConstantPool(["first"]),
            new ActionPush([PushValue.Constant8(0)]),
            new ActionPop(),
            new ActionConstantPool(["second"]),
            new ActionPush([PushValue.Constant8(0)]),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.ConstantPools.TryGetEntries(
            new ActionIndex(1),
            out var first).ShouldBeTrue();
        first.ShouldBe(["first"]);
        result.ConstantPools.TryGetEntries(
            new ActionIndex(4),
            out var second).ShouldBeTrue();
        second.ShouldBe(["second"]);
    }

    [Theory]
    [InlineData("first", true)]
    [InlineData("second", false)]
    public void Constant_pool_analysis_merges_only_identical_branch_states(
        string alternate,
        bool expectedKnown)
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionConstantPool(["first"]),
            new ActionJump(0),
            new ActionConstantPool([alternate]),
            new ActionPush([PushValue.Constant8(0)]),
            new ActionPop()
        };
        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[5]);
        SetBranch(actions, index: 4, offsets[6]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var isKnown = result.ConstantPools.TryGetEntries(
            new ActionIndex(6),
            out var entries);

        isKnown.ShouldBe(expectedKnown);
        if (expectedKnown)
            entries.ShouldBe(["first"]);
    }

    [Fact]
    public void Value_analysis_propagates_identical_constants_through_phi()
    {
        var thenBody = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(7)]),
            new ActionStoreRegister(5),
            new ActionPop()
        };

        var elseBody = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(7)]),
            new ActionStoreRegister(5),
            new ActionPop()
        };

        var mergeBody = new Avm1Action[]
        {
            new ActionPush([PushValue.Register(5)]),
            new ActionPop()
        };

        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0)
        };
        actions.AddRange(thenBody);
        actions.Add(new ActionJump(0));
        actions.AddRange(elseBody);
        actions.AddRange(mergeBody);

        var offsets = ComputeOffsets(actions);
        var ifIndex = 2;
        var jumpIndex = ifIndex + 1 + thenBody.Length;
        var elseStart = jumpIndex + 1;
        var mergeStart = elseStart + elseBody.Length;

        SetBranch(actions, ifIndex, offsets[elseStart]);
        SetBranch(actions, jumpIndex, offsets[mergeStart]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var registerLoad = result.TacIr.Instructions.Single(i => i.Op == Avm1TacOp.LoadRegister).Result;

        result.RegisterSsa.PhiNodes.Count.ShouldBe(1);
        result.ValueAnalysis[registerLoad].Type.ShouldBe(Avm1InferredType.Integer);
        result.ValueAnalysis[registerLoad].ConstantKind.ShouldBe(Avm1ConstantKind.Integer);
        result.ValueAnalysis[registerLoad].IntegerValue.ShouldBe(7);
    }

    [Fact]
    public void Structured_as2_emitter_prints_simple_assignments()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("answer")]),
            new ActionPush([PushValue.Integer(40)]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionAdd2(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "answer = 42;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_member_assignments()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetMember(),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("bad-name")]),
            new ActionPush([PushValue.Integer(6)]),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "obj.field = 5;",
            "obj[\"bad-name\"] = 6;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_register_transported_member_assignment_chain()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("disableSelection")]),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("disableInput")]),
            new ActionPush([PushValue.Boolean(true)]),
            new ActionStoreRegister(5),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.AssignMember)
            .ShouldBe(2);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "outer.disableSelection = inner.disableInput = true;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_inlines_computed_rhs_of_member_assignment_chain()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("disableSelection")]),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("disableInput")]),
            new ActionPush([PushValue.String("source")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("value")]),
            new ActionGetMember(),
            new ActionStoreRegister(5),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "outer.disableSelection = inner.disableInput = source.value;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_register_assignment_inside_member_chain()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("seed")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionPush([PushValue.String("source")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionSetMember(),
            new ActionPush([PushValue.String("saved")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 7, offsets[15]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc2_ = seed;",
            "if (condition)",
            "{",
            "    outer.field = _loc2_ = source;",
            "}",
            "saved = _loc2_;",
            string.Empty
        ]));
        result.StructuredAst.Nodes.ShouldContain(node =>
            node.Kind == Avm1AstNodeKind.AssignRegister &&
            node.Block.Value == 2);
    }

    [Fact]
    public void Structured_as2_emitter_recovers_register_assignment_as_method_receiver()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Undefined()]),
            new ActionStoreRegister(3),
            new ActionPop(),
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Register(4)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.Register(1)]),
            new ActionPush([PushValue.Register(2)]),
            new ActionNewMethod(),
            new ActionStoreRegister(3),
            new ActionPush([PushValue.String("run")]),
            new ActionCallMethod(),
            new ActionPop(),
            new ActionPush([PushValue.String("current")]),
            new ActionPush([PushValue.Register(3)]),
            new ActionSetVariable(),
            new ActionJump(0)
        };
        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 6, offsets[^1] + Len(actions[^1]));
        SetBranch(actions, index: 20, offsets[3]);
        var context = new FunctionContext(
            (FunctionFlags)0,
            [
                new FunctionParameter(1, "plugins"),
                new FunctionParameter(2, "key"),
                new FunctionParameter(4, "value")
            ]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6, context);
        var diagnostic = string.Join(Environment.NewLine,
            result.SymbolTable.Symbols.Select(symbol =>
                $"symbol {symbol.Id}: r{symbol.Register}v{symbol.Version} " +
                $"{symbol.Name} declared={symbol.IsDeclared}")) +
            Environment.NewLine +
            string.Join(Environment.NewLine,
                result.RegisterSsa.Accesses.Select(access => access.ToString())) +
            Environment.NewLine +
            result.GetStructuredAstDebugText();

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc3_;",
            "while (condition)",
            "{",
            "    (_loc3_ = new plugins[key]()).run(value);",
            "    current = _loc3_;",
            "}",
            string.Empty
        ]), diagnostic);
    }

    [Fact]
    public void Structured_as2_emitter_recovers_pseudo_register_assignment_expression()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("__reg0")]),
            new ActionPush([PushValue.String("source")]),
            new ActionGetVariable(),
            new ActionStoreRegister(0),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(0)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionStrictEquals(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.ShouldContain(node =>
            node.Kind == Avm1AstNodeKind.AssignVariable);
        result.GetStructuredAs2Text().ShouldBe(
            $"result = (__reg0 = source) === 1;{Environment.NewLine}");
    }

    [Fact]
    public void Structured_as2_emitter_uses_register_backed_parameter_as_pseudo_register_source()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("__reg0")]),
            new ActionPush([PushValue.Register(1)]),
            new ActionStoreRegister(0),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(0)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionStrictEquals(),
            new ActionSetVariable()
        };
        var context = new FunctionContext(
            (FunctionFlags)0,
            [new FunctionParameter(1, "nav")]);

        var result = Avm1Decompiler.DecompileMethod(
            actions,
            swfVersion: 6,
            context);

        result.GetStructuredAs2Text().ShouldBe(
            $"result = (__reg0 = nav) === 1;{Environment.NewLine}");
        result.ProjectSource().GetAs2Text().ShouldBe(
            $"result = (__reg0 = nav) === 1;{Environment.NewLine}");
    }

    [Fact]
    public void Structured_as2_emitter_reuses_first_register_store_for_retained_stack_value()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("getValue")]),
            new ActionCallFunction(),
            new ActionStoreRegister(2),
            new ActionPush([PushValue.Undefined()]),
            new ActionEquals2(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("saved")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc2_ = getValue();",
            "result = _loc2_ == undefined;",
            "saved = _loc2_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_compound_register_assignment_from_stack_transport()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("seed")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionBitURShift(),
            new ActionStoreRegister(2),
            new ActionPush([PushValue.Integer(1)]),
            new ActionBitAnd(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("saved")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc2_ = seed;",
            "result = (_loc2_ >>>= 1) & 1;",
            "saved = _loc2_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_does_not_replace_stack_value_after_register_overwrite()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("seed")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPush([PushValue.String("before")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("other")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionSetVariable()
        };

        var text = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6)
            .GetStructuredAs2Text();

        text.ShouldContain("result = v");
        text.ShouldNotContain("result = _loc2_v1 + 1;");
    }

    [Fact]
    public void Structured_as2_emitter_declares_reused_register_before_member_chain()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("seed")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.String("initial")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionPush([PushValue.String("source")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionSetMember(),
            new ActionPush([PushValue.String("saved")]),
            new ActionPush([PushValue.Register(2)]),
            new ActionSetVariable()
        };

        var text = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6)
            .GetStructuredAs2Text();

        text.ShouldContain("var _loc2_v2;");
        text.ShouldContain("outer.field = _loc2_v2 = source;");
    }

    [Fact]
    public void Structured_as2_emitter_rejects_mismatched_pseudo_register_transport()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("notReg0")]),
            new ActionPush([PushValue.String("source")]),
            new ActionGetVariable(),
            new ActionStoreRegister(0),
            new ActionSetVariable(),
            new ActionPush([PushValue.Register(0)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionStrictEquals(),
            new ActionSetVariable()
        };

        var text = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6)
            .GetStructuredAs2Text();

        text.ShouldContain("notReg0 = ");
        text.ShouldNotContain("(notReg0 = source) === 1");
    }

    [Fact]
    public void Structured_as2_emitter_recovers_nested_member_prefix_updates()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionGetMember(),
            new ActionIncrement(),
            new ActionStoreRegister(5),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetMember(),

            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("other")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("other")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("other")]),
            new ActionGetMember(),
            new ActionDecrement(),
            new ActionStoreRegister(6),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(6)]),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.Prefix)
            .ShouldBe(2);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "obj.count = ++obj.count;",
            "obj.other = --obj.other;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_canonicalizes_standalone_member_update_as_postfix()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionGetMember(),
            new ActionIncrement(),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.ShouldContain(node => node.Kind == Avm1AstNodeKind.Postfix);
        result.StructuredAst.Nodes.ShouldNotContain(node => node.Kind == Avm1AstNodeKind.Prefix);
        result.GetStructuredAs2Text().ShouldBe($"obj.count++;{Environment.NewLine}");
    }

    [Fact]
    public void Structured_as2_emitter_recovers_member_postfix_value_return()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionGetMember(),
            new ActionIncrement(),
            new ActionSetMember(),
            new ActionReturn()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.ShouldContain(node => node.Kind == Avm1AstNodeKind.Postfix);
        result.GetStructuredAs2Text().ShouldBe($"return obj.count++;{Environment.NewLine}");
    }

    [Fact]
    public void Structured_as2_emitter_does_not_recover_prefix_for_different_member_read()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionPush([PushValue.String("other")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionGetMember(),
            new ActionIncrement(),
            new ActionStoreRegister(5),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.ShouldNotContain(node => node.Kind == Avm1AstNodeKind.Prefix);
        result.GetStructuredAs2Text().ShouldBe(
            $"obj.count = obj.count = other.count + 1;{Environment.NewLine}");
    }

    [Fact]
    public void Structured_as2_emitter_does_not_recover_member_update_across_side_effect()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("mutate")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("count")]),
            new ActionGetMember(),
            new ActionIncrement(),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var text = result.GetStructuredAs2Text();

        result.StructuredAst.Nodes.ShouldNotContain(node => node.Kind == Avm1AstNodeKind.Prefix);
        result.StructuredAst.Nodes.ShouldNotContain(node => node.Kind == Avm1AstNodeKind.Postfix);
        text.ShouldContain("mutate();");
        text.ShouldContain("obj.count + 1");
    }

    [Fact]
    public void Structured_as2_emitter_does_not_fold_assignment_chain_when_transport_register_escapes()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("disableSelection")]),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("disableInput")]),
            new ActionPush([PushValue.Boolean(true)]),
            new ActionStoreRegister(5),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetMember(),
            new ActionPush([PushValue.String("saved")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var text = result.GetStructuredAs2Text();

        text.ShouldNotContain("outer.disableSelection = inner.disableInput = true;");
        text.ShouldContain("disableInput = true;");
        text.ShouldContain("disableSelection = true;");
        text.ShouldContain("saved = true;");
    }

    [Fact]
    public void Structured_as2_emitter_prints_member_reads_in_expressions()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("value")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "value = obj.field + 1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_register_assignments()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("source")]),
            new ActionGetVariable(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("target")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc5_ = source;",
            "target = _loc5_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_uses_registers_in_member_assignments()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("source")]),
            new ActionGetVariable(),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc5_ = source;",
            "obj.field = _loc5_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_declares_non_inlined_stack_temporaries()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("source")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionPush([PushValue.String("a")]),
            new ActionStackSwap(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("b")]),
            new ActionStackSwap(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var v1 = source;",
            "a = v1;",
            "b = v1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_return_expressions()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionReturn()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "return obj.field + 1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_function_calls_with_arguments()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.String("foo")]),
            new ActionCallFunction(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "foo(a, b);",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_inlines_ordered_nested_call_arguments()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("second")]),
            new ActionCallFunction(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("first")]),
            new ActionCallFunction(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.String("consume")]),
            new ActionCallFunction(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "consume(first(), second());",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_inlines_ordered_constructor_arguments()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("second")]),
            new ActionCallFunction(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("first")]),
            new ActionCallFunction(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.String("Container")]),
            new ActionNewObject(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "new Container(first(), second());",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_keeps_call_argument_before_independent_statement()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("second")]),
            new ActionCallFunction(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("first")]),
            new ActionCallFunction(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.String("consume")]),
            new ActionCallFunction(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var v2 = second();",
            "probe();",
            "consume(first(), v2);",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_function_call_expressions()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.String("foo")]),
            new ActionCallFunction(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = foo(a, b);",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_method_calls_with_arguments()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("method")]),
            new ActionCallMethod(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "obj.method(a);",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_array_literals_in_stack_order()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("arr")]),
            new ActionPush([PushValue.Integer(30)]),
            new ActionPush([PushValue.Integer(20)]),
            new ActionPush([PushValue.Integer(10)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionInitArray(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.TacIr.Instructions.Select(i => i.Op).ShouldContain(Avm1TacOp.InitArray);
        var array = result.TacIr.Instructions.Single(instruction => instruction.Op is Avm1TacOp.InitArray);
        result.ValueAnalysis[array.Result].Type.ShouldBe(Avm1InferredType.Array);
        result.ValueAnalysis[array.Result].ElementType.ShouldBe(Avm1InferredType.Integer);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "arr = [10, 20, 30];",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_object_literals_in_evaluation_order()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("config")]),
            new ActionPush([PushValue.String("bad-name")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.String("first")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionInitObject(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.TacIr.Instructions.Select(i => i.Op).ShouldContain(Avm1TacOp.InitObject);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "config = {\"bad-name\": 2, first: 1};",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_inlines_ordered_object_literal_values()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("first")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("value")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("second")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("readSecond")]),
            new ActionCallFunction(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionInitObject(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = {first: obj.value, second: readSecond()};",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_keeps_object_value_before_independent_call()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("first")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("value")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.String("second")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionInitObject(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var text = result.GetStructuredAs2Text();

        text.ShouldContain("var v");
        text.ShouldContain("probe();");
        text.ShouldContain("result = {first: v");
        text.ShouldNotContain("result = {first: obj.value");
        text.IndexOf("var v", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("probe();", StringComparison.Ordinal));
    }

    [Fact]
    public void Structured_as2_emitter_inlines_ordered_binary_operands()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("value")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("readRight")]),
            new ActionCallFunction(),
            new ActionAdd2(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = obj.value + readRight();",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_keeps_binary_operand_before_independent_call()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("value")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("readRight")]),
            new ActionCallFunction(),
            new ActionAdd2(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var text = result.GetStructuredAs2Text();

        text.ShouldContain("var v");
        text.ShouldContain("probe();");
        text.ShouldContain("result = v");
        text.ShouldNotContain("result = obj.value + readRight();");
        text.IndexOf("var v", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("probe();", StringComparison.Ordinal));
    }

    [Fact]
    public void Structured_as2_emitter_prints_new_object_expressions()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.String("gfx.controls.Button")]),
            new ActionNewObject(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.TacIr.Instructions.Select(i => i.Op).ShouldContain(Avm1TacOp.NewObject);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = new gfx.controls.Button(a, b);",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_new_method_expressions()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.String("factory")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("create")]),
            new ActionNewMethod(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.TacIr.Instructions.Select(i => i.Op).ShouldContain(Avm1TacOp.NewMethod);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = new factory.create(a, b);",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_new_method_dynamic_names()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("factory")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("bad-name")]),
            new ActionNewMethod(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = new factory[\"bad-name\"]();",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_new_method_computed_names()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.Register(1)]),
            new ActionPush([PushValue.String("methodName")]),
            new ActionGetVariable(),
            new ActionNewMethod(),
            new ActionSetVariable()
        };
        var context = new FunctionContext(
            (FunctionFlags)0,
            [new FunctionParameter(1, "factory")]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6, context);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = new factory[methodName]();",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_preserves_new_object_expression_statements()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.String("Foo")]),
            new ActionNewObject(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "new Foo(a);",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_preserves_new_method_expression_statements()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.String("factory")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("create")]),
            new ActionNewMethod(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "new factory.create(a);",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_canonicalizes_negated_comparisons()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("notEqual")]),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionEquals2(),
            new ActionNot(),
            new ActionSetVariable(),

            new ActionPush([PushValue.String("greaterOrEqual")]),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionLess2(),
            new ActionNot(),
            new ActionSetVariable(),

            new ActionPush([PushValue.String("lessOrEqual")]),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionGreater(),
            new ActionNot(),
            new ActionSetVariable(),

            new ActionPush([PushValue.String("strictNotEqual")]),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionStrictEquals(),
            new ActionNot(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes
            .Where(node => node.Kind is Avm1AstNodeKind.Binary)
            .ShouldAllBe(node => node.Merge.Value == 1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "notEqual = a != b;",
            "greaterOrEqual = a >= b;",
            "lessOrEqual = a <= b;",
            "strictNotEqual = a !== b;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_variable_compound_assignment()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("i")]),
            new ActionPush([PushValue.String("i")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionAdd2(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var statement = result.StructuredAst.Nodes.Single(node =>
            node.Kind is Avm1AstNodeKind.CompoundAssignVariable);

        statement.StartAction.Value.ShouldBe((int)Avm1.ActionOpcode.Add2);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "i += 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Compound_variable_assignment_consumes_its_effectful_left_read()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("i")]),
            new ActionPush([PushValue.String("i")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("delta")]),
            new ActionGetVariable(),
            new ActionAdd2(),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "i += delta;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_member_compound_assignment()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSubtract(),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.ShouldContain(node =>
            node.Kind == Avm1AstNodeKind.CompoundAssignMember);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "obj.field -= 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_inlines_ordered_member_target_before_rhs_call()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("child")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("field")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("createValue")]),
            new ActionCallFunction(),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "obj.child.field = createValue();",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_inlines_ordered_computed_member_before_rhs_call()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("keyHolder")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("prop")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("createValue")]),
            new ActionCallFunction(),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "obj[keyHolder.prop] = createValue();",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_keeps_computed_member_before_independent_effect()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("keyHolder")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("prop")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetMember()
        };

        var text = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6)
            .GetStructuredAs2Text();

        text.ShouldContain("var v5 = keyHolder.prop;");
        text.ShouldContain("probe();");
        text.ShouldContain("[v5] = 1;");
        text.ShouldNotContain("[keyHolder.prop]");
    }

    [Fact]
    public void Structured_as2_emitter_inlines_member_target_across_short_circuit_rhs()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("child")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("visible")]),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionSetMember()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 8, offsets[12]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.PhiNodes.Count.ShouldBe(1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "obj.child.visible = a || b;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_keeps_member_target_before_independent_short_circuit_arm_effect()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("child")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("visible")]),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionSetMember()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 8, offsets[16]);

        var text = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6)
            .GetStructuredAs2Text();

        text.ShouldContain("var v3 = obj.child;");
        text.ShouldContain("probe();");
        text.ShouldContain("v3.visible");
        text.ShouldNotContain("obj.child.visible");
    }

    [Fact]
    public void Structured_as2_emitter_keeps_member_target_before_independent_statement()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("getObj")]),
            new ActionCallFunction(),
            new ActionPush([PushValue.String("field")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var v2 = getObj();",
            "probe();",
            "v2.field = 1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_does_not_merge_repeated_side_effecting_targets()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("getObj")]),
            new ActionCallFunction(),
            new ActionPush([PushValue.String("field")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("getObj")]),
            new ActionCallFunction(),
            new ActionPush([PushValue.String("field")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Integer(1)]),
            new ActionAdd2(),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StructuredAst.Nodes.ShouldNotContain(node =>
            node.Kind == Avm1AstNodeKind.CompoundAssignMember);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "getObj().field = getObj().field + 1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_normalizes_getter_calls_as_property_reads()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("__get__value")]),
            new ActionCallMethod(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = obj.value;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_normalizes_setter_calls_as_property_writes()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(42)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("__set__value")]),
            new ActionCallMethod(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "obj.value = 42;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_suppresses_flash_setter_getter_epilogue()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("changed")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn(),
            new ActionPush([
                PushValue.Integer(0),
                PushValue.Register(1),
                PushValue.String("__get__value")]),
            new ActionCallMethod(),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[6]);
        var context = new FunctionContext(FunctionFlags.PreloadThis, []);

        var unnormalized = Avm1Decompiler.DecompileMethod(
            actions,
            swfVersion: 6,
            context);
        var normalized = Avm1Decompiler.DecompileMethod(
            actions,
            swfVersion: 6,
            context,
            options: new Avm1MethodDecompilationOptions("value"));

        unnormalized.GetStructuredAs2Text().ShouldContain("return this.value;");
        normalized.StructuredAst.Nodes.Count(node => node.Kind == Avm1AstNodeKind.Return)
            .ShouldBe(1);
        normalized.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (changed)",
            "{",
            "    return;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_flattens_setter_tail_after_early_return()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("stop")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn(),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([
                PushValue.Integer(0),
                PushValue.Register(1),
                PushValue.String("__get__value")]),
            new ActionCallMethod(),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[6]);
        var context = new FunctionContext(FunctionFlags.PreloadThis, []);

        var result = Avm1Decompiler.DecompileMethod(
            actions,
            swfVersion: 6,
            context,
            options: new Avm1MethodDecompilationOptions("value"));

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (stop)",
            "{",
            "    return;",
            "}",
            "z = 1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_inverts_terminal_else_before_flattening_tail()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("keep")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn(),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([
                PushValue.Integer(0),
                PushValue.Register(1),
                PushValue.String("__get__value")]),
            new ActionCallMethod(),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[5]);
        var context = new FunctionContext(FunctionFlags.PreloadThis, []);

        var result = Avm1Decompiler.DecompileMethod(
            actions,
            swfVersion: 6,
            context,
            options: new Avm1MethodDecompilationOptions("value"));

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (!keep)",
            "{",
            "    return;",
            "}",
            "z = 1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_preserves_setter_return_from_other_receiver()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("other")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("__get__value")]),
            new ActionCallMethod(),
            new ActionReturn()
        };
        var context = new FunctionContext(FunctionFlags.PreloadThis, []);

        var result = Avm1Decompiler.DecompileMethod(
            actions,
            swfVersion: 6,
            context,
            options: new Avm1MethodDecompilationOptions("value"));

        result.GetStructuredAs2Text().ShouldBe(
            $"return other.value;{Environment.NewLine}");
    }

    [Fact]
    public void Stack_ir_uses_invalid_handles_for_absent_operands()
    {
        var actions = new Avm1Action[]
        {
            new ActionConstantPool(["foo"]),
            new ActionPush([PushValue.Register(1)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.Constant8(0)]),
            new ActionCallFunction(),
            new ActionPop()
        };
        var context = new FunctionContext(
            (FunctionFlags)0,
            [new FunctionParameter(1, "arg")]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6, context);

        result.TacIr.Instructions[0].Result.ShouldBe(ValueIndex.Invalid);
        result.TacIr.Instructions[1].Operand0.ShouldBe(ValueIndex.Invalid);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "foo(arg);",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_anonymous_function_member_assignment()
    {
        var functionBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(1)]),
                new ActionReturn()
            ],
            swfVersion: 7);
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("onPress")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 0,
                flags: (FunctionFlags)0,
                parameters: [],
                body: functionBody),
            new ActionSetMember()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);

        result.TacIr.Instructions.Select(instruction => instruction.Op)
            .ShouldContain(Avm1TacOp.FunctionLiteral);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "obj.onPress = function()",
            "{",
            "    return 1;",
            "};",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_declares_reused_anonymous_function_temporary()
    {
        var functionBody = Avm1Action.EncodeCollection(
            [
                new ActionPush([PushValue.Integer(1)]),
                new ActionReturn()
            ],
            swfVersion: 7);
        var actions = new Avm1Action[]
        {
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 0,
                flags: (FunctionFlags)0,
                parameters: [],
                body: functionBody),
            new ActionPushDuplicate(),
            new ActionPush([PushValue.String("first")]),
            new ActionStackSwap(),
            new ActionSetVariable(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.String("second")]),
            new ActionStackSwap(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);
        var text = result.GetStructuredAs2Text();

        text.ShouldContain("var v0 = function()");
        text.ShouldContain("first = v0;");
        text.ShouldContain("probe();");
        text.ShouldContain("second = v0;");
    }

    [Fact]
    public void Structured_as2_emitter_preserves_arguments_across_cast_expression()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("TL")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.String("MovieClip")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionCastOp(),
            new ActionPush([PushValue.String("Lock")]),
            new ActionCallMethod(),
            new ActionPop()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);

        result.TacIr.Instructions.Select(instruction => instruction.Op)
            .ShouldContain(Avm1TacOp.Cast);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "MovieClip(obj).Lock(\"TL\");",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_inlines_ordered_cast_function_before_argument_call()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("Type")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("createValue")]),
            new ActionCallFunction(),
            new ActionCastOp(),
            new ActionSetVariable()
        };

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = Type(createValue());",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_keeps_cast_function_before_independent_effect()
    {
        var actions = new Avm1Action[]
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("Type")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionCastOp(),
            new ActionSetVariable()
        };

        var text = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7)
            .GetStructuredAs2Text();

        text.ShouldContain("var v2 = Type;");
        text.ShouldContain("probe();");
        text.ShouldContain("result = v2(obj);");
        text.ShouldNotContain("result = Type(obj);");
    }

    [Fact]
    public void Structured_as2_emitter_recovers_stack_phi_as_conditional_variable_value()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[6]);
        SetBranch(actions, index: 5, offsets[7]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.PhiNodes.Count.ShouldBe(1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = condition ? 1 : 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_swaps_ternary_arms_to_avoid_negated_comparison()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 7, offsets[10]);
        SetBranch(actions, index: 9, offsets[11]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = a < b ? 2 : 1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_stack_phi_as_conditional_member_value()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetMember()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 5, offsets[8]);
        SetBranch(actions, index: 7, offsets[9]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.PhiNodes.Count.ShouldBe(1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "obj.field = condition ? 1 : 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_does_not_mistake_nested_ternary_for_loop_continue()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(0)]),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.Register(1), PushValue.Integer(2)]),
            new ActionLess2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("tail")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionJump(0),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(9999)]),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("method")]),
            new ActionCallMethod(),
            new ActionPop(),
            new ActionPush([PushValue.Register(1)]),
            new ActionIncrement(),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionJump(0),
            new ActionEnd()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 6, offsets[31]);
        SetBranch(actions, index: 11, offsets[14]);
        SetBranch(actions, index: 13, offsets[20]);
        SetBranch(actions, index: 16, offsets[19]);
        SetBranch(actions, index: 18, offsets[20]);
        SetBranch(actions, index: 30, offsets[3]);

        var text = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6)
            .GetStructuredAs2Text();

        text.ShouldContain("obj.method(outer ? (inner ? 1 : 9999) : 1");
        text.ShouldNotContain("if (!outer)");
        text.ShouldNotContain("continue;");
    }

    [Fact]
    public void Structured_as2_emitter_recovers_cascaded_two_input_phi_ternary()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("default")]),
            new ActionJump(0),
            new ActionPush([PushValue.String("focused")]),
            new ActionJump(0),
            new ActionPush([PushValue.String("disabled")]),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[10]);
        SetBranch(actions, index: 5, offsets[8]);
        SetBranch(actions, index: 7, offsets[9]);
        SetBranch(actions, index: 9, offsets[11]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.PhiNodes.Count.ShouldBe(2);
        result.GetStructuredAs2Text().ShouldBe(
            "return outer ? \"disabled\" : inner ? \"focused\" : \"default\";" +
            Environment.NewLine);
    }

    [Fact]
    public void Structured_as2_emitter_recovers_stack_phi_as_conditional_register_value()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(1),
            new ActionPop(),
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Register(1)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[5]);
        SetBranch(actions, index: 4, offsets[6]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.PhiNodes.Count.ShouldBe(1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc1_ = condition ? 1 : 2;",
            "result = _loc1_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_short_circuit_and_from_stack_phi()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 5, offsets[9]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = a && b;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_short_circuit_or_from_stack_phi()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 4, offsets[8]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "result = a || b;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_nested_short_circuit_from_stack_phi()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("c")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("hit")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 4, offsets[13]);
        SetBranch(actions, index: 9, offsets[13]);
        SetBranch(actions, index: 14, offsets[18]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.PhiNodes.Single().IncomingValues.Count.ShouldBe(3);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (a && (b || c))",
            "{",
            "    hit = 1;",
            "}",
            "after = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_collapses_register_or_undefined_phi()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Undefined()]),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(0), PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.String("source")]),
            new ActionGetVariable(),
            new ActionStoreRegister(2),
            new ActionPop(),
            new ActionPush([PushValue.Register(2)]),
            new ActionStoreRegister(0),
            new ActionPop(),
            new ActionPush([PushValue.Register(0)]),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[5]);
        SetBranch(actions, index: 4, offsets[13]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var text = result.GetStructuredAs2Text();
        var normalized = Avm1SourceNormalizer.Normalize(
            result.ProjectSource(),
            CancellationToken.None).Method.GetAs2Text();

        text.ShouldContain("return _loc2_;");
        text.ShouldNotContain("_loc0_");
        text.ShouldNotContain(" = v");
        normalized.ShouldContain("return local0;");
        normalized.ShouldNotContain("_loc0_");
    }

    [Fact]
    public void Stack_ssa_does_not_merge_unreachable_predecessor_into_reachable_block()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Integer(2)]),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(99)]),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 1, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.PhiNodes.ShouldBeEmpty();
        result.GetStructuredAs2Text().ShouldBe($"return 2;{Environment.NewLine}");
    }

    [Fact]
    public void Structured_as2_emitter_recovers_short_circuit_through_intermediate_phi()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("c")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("d")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("hit")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[19]);
        SetBranch(actions, index: 9, offsets[13]);
        SetBranch(actions, index: 15, offsets[19]);
        SetBranch(actions, index: 20, offsets[24]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.StackIr.PhiNodes.Count.ShouldBe(2);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (a || b && c && d)",
            "{",
            "    hit = 1;",
            "}",
            "after = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_rejects_nested_short_circuit_with_independent_call()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionPop(),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionPushDuplicate(),
            new ActionIf(0),
            new ActionPop(),
            new ActionPush([PushValue.String("c")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("hit")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("after")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 4, offsets[17]);
        SetBranch(actions, index: 13, offsets[17]);
        SetBranch(actions, index: 18, offsets[22]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var text = result.GetStructuredAs2Text();

        text.ShouldContain("probe();");
        text.ShouldNotContain("a && (b || c)");
    }

    [Fact]
    public void Structured_as2_emitter_coalesces_register_phi_versions()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[7]);
        SetBranch(actions, index: 6, offsets[10]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.RegisterSsa.PhiNodes.Count.ShouldBe(1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (!condition)",
            "{",
            "    var _loc5_ = 1;",
            "}",
            "else",
            "{",
            "    _loc5_ = 2;",
            "}",
            "result = _loc5_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_coalesces_undefined_declaration_with_register_phi()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.Undefined()]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 5, offsets[10]);
        SetBranch(actions, index: 9, offsets[13]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "var _loc5_;",
            "if (!condition)",
            "{",
            "    _loc5_ = 1;",
            "}",
            "else",
            "{",
            "    _loc5_ = 2;",
            "}",
            "result = _loc5_;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_nested_if_inside_while()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("loop")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[12]);
        SetBranch(actions, index: 7, offsets[11]);
        SetBranch(actions, index: 11, offsets[0]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "while (loop)",
            "{",
            "    if (flag)",
            "    {",
            "        y = 1;",
            "    }",
            "}",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_terminal_if_with_end_merge()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[^1] + Len(actions[^1]));

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.RegionAnalysis.IfRegions.Count.ShouldBe(1);
        result.RegionAnalysis.IfRegions[0].Merge.ShouldBe(BlockIndex.Invalid);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (flag)",
            "{",
            "    y = 1;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_terminal_if_else_with_virtual_exit_merge()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("condition")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        var endOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 2, offsets[7]);
        SetBranch(actions, index: 6, endOffset);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.RegionAnalysis.IfRegions.Count.ShouldBe(1);
        result.RegionAnalysis.IfRegions[0].Merge.ShouldBe(BlockIndex.Invalid);
        result.RegionAnalysis.IfRegions[0].HasElse.ShouldBeTrue();
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (!condition)",
            "{",
            "    y = 1;",
            "}",
            "else",
            "{",
            "    y = 2;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_keeps_looping_terminal_arm_inside_if_else()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("loop")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionReturn(),
            new ActionPush([PushValue.Integer(2)]),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[13]);
        SetBranch(actions, index: 6, offsets[11]);
        SetBranch(actions, index: 10, offsets[3]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var outer = result.RegionAnalysis.IfRegions.Single(region => region.Header == new BlockIndex(0));

        outer.Merge.ShouldBe(BlockIndex.Invalid);
        outer.HasElse.ShouldBeTrue();
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (!flag)",
            "{",
            "    while (loop)",
            "    {",
            "        y = 1;",
            "    }",
            "    return 1;",
            "}",
            "else",
            "{",
            "    return 2;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_flattens_nested_else_branch_as_else_if()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("a")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("b")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        var endOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 2, offsets[7]);
        SetBranch(actions, index: 6, endOffset);
        SetBranch(actions, index: 9, offsets[14]);
        SetBranch(actions, index: 13, endOffset);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (!a)",
            "{",
            "    y = 1;",
            "}",
            "else if (!b)",
            "{",
            "    y = 2;",
            "}",
            "else",
            "{",
            "    y = 3;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_strict_equality_chain_as_switch()
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildThreeCaseChain(strictEquality: true),
            swfVersion: 6);

        result.SwitchAnalysis.Regions.Count.ShouldBe(1);
        result.SwitchAnalysis.Regions[0].Cases.Count.ShouldBe(3);
        result.SwitchAnalysis.Regions[0].DefaultEntry.ShouldNotBe(result.SwitchAnalysis.Regions[0].Merge);
        result.StructuredAst.Nodes.ShouldContain(node => node.Kind == Avm1AstNodeKind.Switch);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "before = 0;",
            "switch (kind)",
            "{",
            "    case \"a\":",
            "        y = 1;",
            "        break;",
            "    case \"b\":",
            "        y = 2;",
            "        break;",
            "    case \"c\":",
            "        y = 3;",
            "        break;",
            "    default:",
            "        y = 4;",
            "}",
            "z = 5;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_grouped_switch_labels()
    {
        var actions = BuildDirectThreeCaseSwitch(
            bodyByCase: [0, 0, 1],
            bodies: [
                new SwitchBodySpec([
                    new ActionPush([PushValue.String("y")]),
                    new ActionPush([PushValue.Integer(12)]),
                    new ActionSetVariable()
                ], JumpsToMerge: true),
                new SwitchBodySpec([
                    new ActionPush([PushValue.String("y")]),
                    new ActionPush([PushValue.Integer(3)]),
                    new ActionSetVariable()
                ], JumpsToMerge: true)
            ],
            defaultBody: new SwitchBodySpec([
                new ActionPush([PushValue.String("y")]),
                new ActionPush([PushValue.Integer(4)]),
                new ActionSetVariable()
            ], JumpsToMerge: true));

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.SwitchAnalysis.Regions.Count.ShouldBe(1);
        result.SwitchAnalysis.Regions[0].Cases.Select(@case => @case.ExitFlags).ShouldBe([
            Avm1SwitchCaseExitFlags.Grouped,
            Avm1SwitchCaseExitFlags.Break,
            Avm1SwitchCaseExitFlags.Break
        ]);
        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.SwitchCase).ShouldBe(3);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            "    case \"b\":",
            "        y = 12;",
            "        break;",
            "    case \"c\":",
            "        y = 3;",
            "        break;",
            "    default:",
            "        y = 4;",
            "}",
            "z = 5;",
            string.Empty
        ]));
    }

    [Fact]
    public void Switch_analysis_recovers_grouped_symbolic_labels_through_dispatch_register()
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildGroupedTwoCaseSwitchWithDispatchRegister(readRegisterAfterSwitch: false),
            swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Cases.Count.ShouldBe(2);
        region.Cases[0].ExitFlags.ShouldBe(Avm1SwitchCaseExitFlags.Grouped);
        region.DispatchStores.Count.ShouldBe(1);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case CASE_A:",
            "    case CASE_B:",
            "        y = 12;",
            "        break;",
            "    default:",
            "        y = 4;",
            "}",
            "z = 5;",
            string.Empty
        ]));
    }

    [Fact]
    public void Switch_analysis_keeps_two_case_dispatch_as_if_when_register_version_escapes()
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildGroupedTwoCaseSwitchWithDispatchRegister(readRegisterAfterSwitch: true),
            swfVersion: 6);
        var text = result.GetStructuredAs2Text();

        result.SwitchAnalysis.Regions.ShouldBeEmpty();
        text.ShouldNotContain("switch (");
        text.ShouldContain("after = _loc5_;");
    }

    [Fact]
    public void Switch_analysis_hides_scratch_register_but_preserves_escaping_semantic_register()
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildGroupedTwoCaseSwitchWithSemanticAndScratchRegisters(),
            swfVersion: 6);
        var text = result.GetStructuredAs2Text();

        var region = result.SwitchAnalysis.Regions.Single();
        region.Cases.Count.ShouldBe(2);
        region.DispatchStores.Count.ShouldBe(1);
        text.ShouldContain("var _loc2_ = kind;");
        text.ShouldContain("switch (_loc2_)");
        text.ShouldContain("after = _loc2_;");
        text.ShouldNotContain("_loc0_");
        text.ShouldNotContain("var v");
    }

    [Fact]
    public void Switch_analysis_recovers_distinct_two_case_dispatch_register()
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildDistinctTwoCaseSwitchWithDispatchRegister(),
            swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Cases.Count.ShouldBe(2);
        region.DispatchStores.Count.ShouldBe(1);
        region.DefaultEntry.ShouldBe(region.Merge);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            "        y = 1;",
            "        break;",
            "    case \"b\":",
            "        y = 2;",
            "        break;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_switch_fallthrough()
    {
        var actions = BuildDirectThreeCaseSwitch(
            bodyByCase: [0, 1, 2],
            bodies: [
                new SwitchBodySpec([
                    new ActionPush([PushValue.String("x")]),
                    new ActionPush([PushValue.Integer(1)]),
                    new ActionSetVariable()
                ], JumpsToMerge: false),
                new SwitchBodySpec([
                    new ActionPush([PushValue.String("x")]),
                    new ActionPush([PushValue.Integer(2)]),
                    new ActionSetVariable()
                ], JumpsToMerge: true),
                new SwitchBodySpec([
                    new ActionPush([PushValue.String("x")]),
                    new ActionPush([PushValue.Integer(3)]),
                    new ActionSetVariable()
                ], JumpsToMerge: true)
            ],
            defaultBody: new SwitchBodySpec([
                new ActionPush([PushValue.String("x")]),
                new ActionPush([PushValue.Integer(4)]),
                new ActionSetVariable()
            ], JumpsToMerge: true));

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Cases[0].ExitFlags.ShouldBe(Avm1SwitchCaseExitFlags.FallThrough);
        region.Cases[0].BodyBoundary.ShouldBe(region.Cases[1].BodyEntry);
        region.Cases[1].ExitFlags.ShouldBe(Avm1SwitchCaseExitFlags.Break);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            "        x = 1;",
            "    case \"b\":",
            "        x = 2;",
            "        break;",
            "    case \"c\":",
            "        x = 3;",
            "        break;",
            "    default:",
            "        x = 4;",
            "}",
            "z = 5;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_emits_break_for_case_falling_into_switch_merge()
    {
        var actions = BuildDirectThreeCaseSwitch(
            bodyByCase: [2, 0, 1],
            bodies: [
                new SwitchBodySpec([
                    new ActionPush([PushValue.String("x")]),
                    new ActionPush([PushValue.Integer(10)]),
                    new ActionSetVariable()
                ], JumpsToMerge: true),
                new SwitchBodySpec([
                    new ActionPush([PushValue.String("x")]),
                    new ActionPush([PushValue.Integer(20)]),
                    new ActionSetVariable()
                ], JumpsToMerge: true),
                new SwitchBodySpec([
                    new ActionPush([PushValue.String("x")]),
                    new ActionPush([PushValue.Integer(30)]),
                    new ActionSetVariable()
                ], JumpsToMerge: false)
            ],
            defaultBody: new SwitchBodySpec([
                new ActionPush([PushValue.String("x")]),
                new ActionPush([PushValue.Integer(4)]),
                new ActionSetVariable()
            ], JumpsToMerge: true));

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Cases[0].ExitFlags.ShouldBe(Avm1SwitchCaseExitFlags.Break);
        result.ControlFlowGraph[region.Cases[0].BodyEntry].Terminator
            .ShouldBe(Avm1BlockTerminatorKind.FallThrough);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            "        x = 30;",
            "        break;",
            "    case \"b\":",
            "        x = 10;",
            "        break;",
            "    case \"c\":",
            "        x = 20;",
            "        break;",
            "    default:",
            "        x = 4;",
            "}",
            "z = 5;",
            string.Empty
        ]));
    }

    [Theory]
    [InlineData(true, "if (flag)")]
    [InlineData(false, "if (!flag)")]
    public void Structured_as2_emitter_recovers_conditional_break_with_fallthrough(
        bool breakOnTrue,
        string expectedCondition)
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildConditionalBreakFallThroughSwitch(breakOnTrue),
            swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Cases[0].ExitFlags.ShouldBe(Avm1SwitchCaseExitFlags.ConditionalBreak);
        region.Cases[0].BodyBoundary.ShouldBe(region.Cases[1].BodyEntry);
        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.Break).ShouldBe(3);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            $"        {expectedCondition}",
            "        {",
            "            break;",
            "        }",
            "        x = 1;",
            "    case \"b\":",
            "        x = 2;",
            "        break;",
            "    case \"c\":",
            "        x = 3;",
            "        break;",
            "    default:",
            "        x = 4;",
            "}",
            "z = 5;",
            string.Empty
        ]));
    }

    [Theory]
    [InlineData(true, "if (flag)")]
    [InlineData(false, "if (!flag)")]
    public void Structured_as2_emitter_recovers_visible_switch_break_arm(
        bool breakOnTrue,
        string expectedCondition)
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildVisibleBreakArmFallThroughSwitch(breakOnTrue),
            swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Cases[0].ExitFlags.ShouldBe(Avm1SwitchCaseExitFlags.ConditionalBreak);
        region.Cases[0].BodyBoundary.ShouldBe(region.Cases[1].BodyEntry);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            $"        {expectedCondition}",
            "        {",
            "            y = 9;",
            "            break;",
            "        }",
            "        x = 1;",
            "    case \"b\":",
            "        x = 2;",
            "        break;",
            "    case \"c\":",
            "        x = 3;",
            "        break;",
            "    default:",
            "        x = 4;",
            "}",
            "z = 5;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_keeps_switch_breaks_inside_while_scope()
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildSwitchInsideWhile(),
            swfVersion: 6);

        result.RegionAnalysis.WhileRegions.Count.ShouldBe(1);
        result.SwitchAnalysis.Regions.Count.ShouldBe(1);
        result.StructuredAst.Nodes.Count(node => node.Kind is Avm1AstNodeKind.Break).ShouldBe(3);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "while (loop)",
            "{",
            "    switch (kind)",
            "    {",
            "        case \"a\":",
            "            x = 1;",
            "            break;",
            "        case \"b\":",
            "            x = 2;",
            "            break;",
            "        case \"c\":",
            "            x = 3;",
            "            break;",
            "        default:",
            "            x = 4;",
            "    }",
            "    y = 5;",
            "}",
            "z = 6;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_labels_outer_loop_break_from_switch_case()
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildSwitchInsideWhile(includeOuterLoopExits: true),
            swfVersion: 6);

        result.RegionAnalysis.WhileRegions.Count.ShouldBe(1);
        result.SwitchAnalysis.Regions.Count.ShouldBe(1);
        result.SwitchAnalysis.Regions[0].Cases[0].ExitFlags.ShouldBe(
            Avm1SwitchCaseExitFlags.Break | Avm1SwitchCaseExitFlags.Escaping);
        result.SwitchAnalysis.Regions[0].Cases[1].ExitFlags.ShouldBe(
            Avm1SwitchCaseExitFlags.Break | Avm1SwitchCaseExitFlags.Escaping);
        var loopHeader = result.RegionAnalysis.WhileRegions[0].Header;
        var source = result.GetStructuredAs2Text();
        var labeledBreaks = result.StructuredAst.Nodes.Where(node =>
            node.Kind is Avm1AstNodeKind.Break && node.Block.IsValid).ToArray();
        labeledBreaks.Length.ShouldBe(1, source);
        labeledBreaks[0].Block.ShouldBe(loopHeader);
        result.StructuredAst.Nodes.Single(node => node.Kind is Avm1AstNodeKind.Continue)
            .Block.ShouldBe(BlockIndex.Invalid);
        source.ShouldBe(string.Join(Environment.NewLine, [
            "loop_b0:",
            "while (loop)",
            "{",
            "    switch (kind)",
            "    {",
            "        case \"a\":",
            "            if (skip)",
            "            {",
            "                continue;",
            "            }",
            "            x = 1;",
            "            break;",
            "        case \"b\":",
            "            if (done)",
            "            {",
            "                break loop_b0;",
            "            }",
            "            x = 2;",
            "            break;",
            "        case \"c\":",
            "            x = 3;",
            "            break;",
            "        default:",
            "            x = 4;",
            "    }",
            "    y = 5;",
            "}",
            "z = 6;",
            string.Empty
        ]));
    }

    [Theory]
    [InlineData(
        false,
        false,
        false,
        Avm1SwitchCaseExitFlags.Break | Avm1SwitchCaseExitFlags.Terminating)]
    [InlineData(
        false,
        true,
        true,
        Avm1SwitchCaseExitFlags.FallThrough | Avm1SwitchCaseExitFlags.Terminating)]
    [InlineData(
        true,
        true,
        false,
        Avm1SwitchCaseExitFlags.Break |
            Avm1SwitchCaseExitFlags.FallThrough |
            Avm1SwitchCaseExitFlags.Terminating)]
    public void Structured_as2_emitter_recovers_composable_switch_case_exits(
        bool conditionalBreak,
        bool fallsThrough,
        bool throws,
        Avm1SwitchCaseExitFlags expectedExitFlags)
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildMixedTerminalExitSwitch(conditionalBreak, fallsThrough, throws),
            swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Cases[0].ExitFlags.ShouldBe(expectedExitFlags);
        region.Cases[0].BodyBoundary.ShouldBe(
            fallsThrough ? region.Cases[1].BodyEntry : region.Merge);

        var expected = new List<string>
        {
            "switch (kind)",
            "{",
            "    case \"a\":",
            "        if (terminal)",
            "        {",
            throws ? "            throw \"bad\";" : "            return 1;",
            "        }"
        };
        if (conditionalBreak)
        {
            expected.AddRange([
                "        if (stop)",
                "        {",
                "            break;",
                "        }"
            ]);
        }

        expected.Add("        x = 1;");
        if (!fallsThrough)
            expected.Add("        break;");
        expected.AddRange([
            "    case \"b\":",
            "        x = 2;",
            "        break;",
            "    case \"c\":",
            "        x = 3;",
            "        break;",
            "    default:",
            "        x = 4;",
            "}",
            "z = 5;",
            string.Empty
        ]);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, expected));
    }

    [Fact]
    public void Structured_as2_emitter_omits_break_after_returning_switch_case()
    {
        var actions = BuildDirectThreeCaseSwitch(
            bodyByCase: [0, 1, 2],
            bodies: [
                new SwitchBodySpec([
                    new ActionPush([PushValue.Integer(1)]),
                    new ActionReturn()
                ], JumpsToMerge: false),
                new SwitchBodySpec([
                    new ActionPush([PushValue.String("y")]),
                    new ActionPush([PushValue.Integer(2)]),
                    new ActionSetVariable()
                ], JumpsToMerge: true),
                new SwitchBodySpec([
                    new ActionPush([PushValue.String("bad")]),
                    new ActionThrow()
                ], JumpsToMerge: false)
            ],
            defaultBody: new SwitchBodySpec([
                new ActionPush([PushValue.String("y")]),
                new ActionPush([PushValue.Integer(4)]),
                new ActionSetVariable()
            ], JumpsToMerge: true));

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.SwitchAnalysis.Regions.Single().Cases[0].ExitFlags
            .ShouldBe(Avm1SwitchCaseExitFlags.Terminating);
        result.SwitchAnalysis.Regions.Single().Cases[2].ExitFlags
            .ShouldBe(Avm1SwitchCaseExitFlags.Terminating);
        result.TacIr.Instructions.ShouldContain(instruction => instruction.Op == Avm1TacOp.Throw);
        result.StructuredAst.Nodes.ShouldContain(node => node.Kind == Avm1AstNodeKind.Throw);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            "        return 1;",
            "    case \"b\":",
            "        y = 2;",
            "        break;",
            "    case \"c\":",
            "        throw \"bad\";",
            "    default:",
            "        y = 4;",
            "}",
            "z = 5;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_fully_terminating_switch_without_merge()
    {
        static SwitchBodySpec Returning(int value) => new([
            new ActionPush([PushValue.Integer(value)]),
            new ActionReturn()
        ], JumpsToMerge: false);

        var result = Avm1Decompiler.DecompileMethod(
            BuildDirectThreeCaseSwitch(
                bodyByCase: [0, 1, 2],
                bodies: [Returning(1), Returning(2), Returning(3)],
                defaultBody: Returning(4),
                includeContinuation: false),
            swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Merge.ShouldBe(BlockIndex.Invalid);
        region.Cases.ShouldAllBe(@case => @case.ExitFlags == Avm1SwitchCaseExitFlags.Terminating);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            "        return 1;",
            "    case \"b\":",
            "        return 2;",
            "    case \"c\":",
            "        return 3;",
            "    default:",
            "        return 4;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_default_only_switch_merge()
    {
        static SwitchBodySpec Returning(int value) => new([
            new ActionPush([PushValue.Integer(value)]),
            new ActionReturn()
        ], JumpsToMerge: false);

        var result = Avm1Decompiler.DecompileMethod(
            BuildDirectThreeCaseSwitch(
                bodyByCase: [0, 1, 2],
                bodies: [Returning(1), Returning(2), Returning(3)],
                defaultBody: new SwitchBodySpec([], JumpsToMerge: true)),
            swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Merge.IsValid.ShouldBeTrue();
        region.DefaultEntry.ShouldBe(region.Merge);
        region.Cases.ShouldAllBe(@case =>
            @case.ExitFlags == Avm1SwitchCaseExitFlags.Terminating);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            "        return 1;",
            "    case \"b\":",
            "        return 2;",
            "    case \"c\":",
            "        return 3;",
            "}",
            "z = 5;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_terminal_switch_breaks_at_virtual_exit()
    {
        static SwitchBodySpec Assigning(int value) => new([
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(value)]),
            new ActionSetVariable()
        ], JumpsToMerge: true);

        var result = Avm1Decompiler.DecompileMethod(
            BuildDirectThreeCaseSwitch(
                bodyByCase: [0, 1, 2],
                bodies: [Assigning(1), Assigning(2), Assigning(3)],
                defaultBody: Assigning(4),
                includeContinuation: false),
            swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Merge.ShouldBe(BlockIndex.Invalid);
        region.DefaultEntry.ShouldNotBe(region.Merge);
        region.Cases.ShouldAllBe(@case => @case.ExitFlags == Avm1SwitchCaseExitFlags.Break);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            "        y = 1;",
            "        break;",
            "    case \"b\":",
            "        y = 2;",
            "        break;",
            "    case \"c\":",
            "        y = 3;",
            "        break;",
            "    default:",
            "        y = 4;",
            "        break;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Switch_analysis_groups_terminal_default_trampoline_with_final_case()
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildTerminalGroupedDefaultSwitchWithDispatchRegister(),
            swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Merge.ShouldBe(BlockIndex.Invalid);
        region.DispatchStores.Count.ShouldBe(1);
        region.DefaultEntry.ShouldBe(region.Cases[^1].BodyEntry);
        region.Cases[^1].ExitFlags.ShouldBe(Avm1SwitchCaseExitFlags.Grouped);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            "        y = 1;",
            "        break;",
            "    case \"b\":",
            "        y = 2;",
            "        break;",
            "    case \"c\":",
            "    default:",
            "        y = 3;",
            "        break;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_recovers_virtual_exit_break_inside_default()
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildTwoCaseSwitchWithGuardedTerminalDefault(),
            swfVersion: 6);

        var region = result.SwitchAnalysis.Regions.Single();
        region.Merge.ShouldBe(BlockIndex.Invalid);
        region.DefaultEntry.ShouldNotBe(region.Merge);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "switch (kind)",
            "{",
            "    case \"a\":",
            "        y = 1;",
            "        break;",
            "    case \"b\":",
            "        y = 2;",
            "        break;",
            "    default:",
            "        if (skip)",
            "        {",
            "            break;",
            "        }",
            "        x = 3;",
            "        break;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Dead_code_elimination_keeps_throw_operand_definition()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("boom")]),
            new ActionThrow()
        ], swfVersion: 7);

        var load = result.TacIr.Instructions.Single(instruction => instruction.Op == Avm1TacOp.LoadConstant);
        var @throw = result.TacIr.Instructions.Single(instruction => instruction.Op == Avm1TacOp.Throw);
        var dead = Avm1DeadCodeElimination.Run(
            result.TacIr,
            result.RegisterSsa,
            result.ValueAnalysis);

        @throw.Operand0.ShouldBe(load.Result);
        dead.ShouldNotContain(load.Index);
        result.GetStructuredAs2Text().ShouldBe($"throw \"boom\";{Environment.NewLine}");
    }

    [Fact]
    public void Dead_code_elimination_removes_store_after_constant_register_read_is_folded()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(100)]),
            new ActionStoreRegister(5),
            new ActionPop(),
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.Register(5)]),
            new ActionSetVariable()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var store = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.StoreRegister);
        var load = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.LoadRegister);
        var dead = Avm1DeadCodeElimination.Run(
            result.TacIr,
            result.RegisterSsa,
            result.ValueAnalysis);

        result.ValueAnalysis[load.Result].IntegerValue.ShouldBe(100);
        dead.ShouldContain(store.Index);
        result.GetStructuredAs2Text().ShouldBe($"result = 100;{Environment.NewLine}");
    }

    [Fact]
    public void Dead_register_store_keeps_side_effecting_source_as_expression_statement()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.String("probe")]),
            new ActionCallFunction(),
            new ActionStoreRegister(5),
            new ActionPop()
        ];

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var call = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.CallFunction);
        var store = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.StoreRegister);
        var dead = Avm1DeadCodeElimination.Run(
            result.TacIr,
            result.RegisterSsa,
            result.ValueAnalysis);

        dead.ShouldNotContain(call.Index);
        dead.ShouldContain(store.Index);
        result.GetStructuredAs2Text().ShouldBe($"probe();{Environment.NewLine}");
    }

    [Fact]
    public void Switch_analysis_keeps_untyped_loose_equality_chain_as_if_else()
    {
        var result = Avm1Decompiler.DecompileMethod(
            BuildThreeCaseChain(strictEquality: false),
            swfVersion: 6);

        result.SwitchAnalysis.Regions.ShouldBeEmpty();
        result.StructuredAst.Nodes.ShouldNotContain(node => node.Kind == Avm1AstNodeKind.Switch);
        result.GetStructuredAs2Text().ShouldNotContain("switch (");
        result.GetStructuredAs2Text().ShouldContain("else if (kind == \"b\")");
    }

    [Fact]
    public void Switch_analysis_recovers_type_proven_numeric_loose_equality_chain()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("seed")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionAdd2(),
            new ActionStoreRegister(5),
            new ActionPop(),

            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionEquals2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionEquals2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.Register(5)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionEquals2(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable(),
            new ActionJump(0),

            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 10, offsets[15]);
        SetBranch(actions, index: 14, offsets[36]);
        SetBranch(actions, index: 19, offsets[24]);
        SetBranch(actions, index: 23, offsets[36]);
        SetBranch(actions, index: 28, offsets[33]);
        SetBranch(actions, index: 32, offsets[36]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.SwitchAnalysis.Regions.Count.ShouldBe(1);
        result.SwitchAnalysis.Regions[0].DispatchStores.Count.ShouldBe(1);
        result.GetStructuredAs2Text().ShouldContain("switch (seed + 0)");
        result.GetStructuredAs2Text().ShouldContain("case 3:");
        result.GetStructuredAs2Text().ShouldNotContain("_loc5_");
    }

    [Fact]
    public void Switch_analysis_uses_class_field_type_for_loose_member_equality()
    {
        var context = new FunctionContext(FunctionFlags.PreloadThis, []);
        var typeEnvironment = Avm1ClassTypeEnvironment.Create(
            "Demo",
            instanceMembers: new Dictionary<string, Avm1ClassMemberType>
            {
                ["_kind"] = new(Avm1InferredType.String)
            });

        var result = Avm1Decompiler.DecompileMethod(
            BuildThreeCaseMemberChain(),
            swfVersion: 6,
            context,
            typeEnvironment);

        result.SwitchAnalysis.Regions.Count.ShouldBe(1);
        result.GetStructuredAs2Text().ShouldContain("switch (this._kind)");
        result.GetStructuredAs2Text().ShouldContain("case \"c\":");
    }

    [Fact]
    public void Method_core_is_reused_across_type_dependent_finalizations()
    {
        var context = new FunctionContext(FunctionFlags.PreloadThis, []);
        var core = Avm1Decompiler.BuildMethodCore(
            BuildThreeCaseMemberChain(),
            swfVersion: 6,
            context);
        var untyped = Avm1Decompiler.DecompileMethod(core);
        var typeEnvironment = Avm1ClassTypeEnvironment.Create(
            "Demo",
            instanceMembers: new Dictionary<string, Avm1ClassMemberType>
            {
                ["_kind"] = new(Avm1InferredType.String)
            });
        var typed = Avm1Decompiler.DecompileMethod(core, typeEnvironment);

        untyped.Core.ShouldBeSameAs(core);
        typed.Core.ShouldBeSameAs(core);
        typed.Instructions.ShouldBeSameAs(untyped.Instructions);
        typed.ControlFlowGraph.ShouldBeSameAs(untyped.ControlFlowGraph);
        typed.DominatorTree.ShouldBeSameAs(untyped.DominatorTree);
        typed.StackIr.ShouldBeSameAs(untyped.StackIr);
        typed.TacIr.ShouldBeSameAs(untyped.TacIr);
        typed.RegisterSsa.ShouldBeSameAs(untyped.RegisterSsa);
        typed.ValueOrigins.ShouldBeSameAs(untyped.ValueOrigins);
        typed.ValueAnalysis.ShouldNotBeSameAs(untyped.ValueAnalysis);
        typed.StructuredAst.ShouldNotBeSameAs(untyped.StructuredAst);
        untyped.SwitchAnalysis.Regions.ShouldBeEmpty();
        typed.SwitchAnalysis.Regions.Count.ShouldBe(1);
        untyped.GetStructuredAs2Text().ShouldContain("else if (this._kind == \"b\")");
        typed.GetStructuredAs2Text().ShouldContain("switch (this._kind)");
    }

    [Fact]
    public void Class_type_analysis_propagates_static_array_element_to_instance_field()
    {
        var initializer = new Avm1Action[]
        {
            new ActionPush([PushValue.String("_global")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("Demo")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 1,
                flags: (FunctionFlags)0,
                parameters: [],
                body: ReadOnlyMemory<byte>.Empty),
            new ActionStoreRegister(1),
            new ActionSetMember(),
            new ActionPush([PushValue.Register(1)]),
            new ActionPush([PushValue.String("NAMES")]),
            new ActionPush([PushValue.String("c")]),
            new ActionPush([PushValue.String("b")]),
            new ActionPush([PushValue.String("a")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionInitArray(),
            new ActionSetMember()
        };
        var initializeMethod = new Avm1Action[]
        {
            new ActionPush([PushValue.Register(1)]),
            new ActionPush([PushValue.String("_kind")]),
            new ActionPush([PushValue.String("Demo")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("NAMES")]),
            new ActionGetMember(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionGetMember(),
            new ActionSetMember()
        };
        var context = new FunctionContext(FunctionFlags.PreloadThis, []);

        var environment = Avm1ClassTypeAnalysis.Build(
            "Demo",
            [
                new Avm1ClassMethodInput(initializer, null, Avm1ClassBodyKind.Initializer),
                new Avm1ClassMethodInput(initializeMethod, context, Avm1ClassBodyKind.Method)
            ],
            swfVersion: 6);

        environment.StaticMembers["NAMES"].ShouldBe(
            new Avm1ClassMemberType(Avm1InferredType.Array, Avm1InferredType.String));
        environment.InstanceMembers["_kind"].ShouldBe(
            new Avm1ClassMemberType(Avm1InferredType.String));
    }

    [Fact]
    public void Switch_analysis_rejects_side_effects_between_case_tests()
    {
        var actions = BuildThreeCaseChain(strictEquality: true);
        actions.InsertRange(13, [
            new ActionPush([PushValue.String("between")]),
            new ActionPush([PushValue.Integer(9)]),
            new ActionSetVariable()
        ]);

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 8, offsets[13]);
        SetBranch(actions, index: 12, offsets[39]);
        SetBranch(actions, index: 21, offsets[26]);
        SetBranch(actions, index: 25, offsets[39]);
        SetBranch(actions, index: 31, offsets[36]);
        SetBranch(actions, index: 35, offsets[39]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.SwitchAnalysis.Regions.ShouldBeEmpty();
        result.GetStructuredAs2Text().ShouldNotContain("switch (");
        result.GetStructuredAs2Text().ShouldContain("between = 9;");
    }

    [Fact]
    public void Structured_as2_emitter_recovers_early_return_guard_before_continuation()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn(),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[6]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.RegionAnalysis.IfRegions.Count.ShouldBe(1);
        result.RegionAnalysis.IfRegions[0].Merge.ShouldBe(new BlockIndex(2));
        result.RegionAnalysis.IfRegions[0].HasElse.ShouldBeFalse();
        var returnNode = result.StructuredAst.Nodes.Single(node => node.Kind is Avm1AstNodeKind.Return);
        Avm1AstArena.GetChildCount(returnNode).ShouldBe(0);
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (flag)",
            "{",
            "    return;",
            "}",
            "z = 1;",
            string.Empty
        ]));
    }

    [Fact]
    public void Region_analysis_recovers_reachable_continuation_when_all_paths_eventually_return()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("early")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionReturn(),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("tail")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionReturn(),
            new ActionPush([PushValue.String("other")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(3)]),
            new ActionReturn(),
            new ActionPush([PushValue.Integer(4)]),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[13]);
        SetBranch(actions, index: 7, offsets[10]);
        SetBranch(actions, index: 16, offsets[19]);
        SetBranch(actions, index: 22, offsets[25]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var outer = result.RegionAnalysis.IfRegions.Single(region => region.Header == new BlockIndex(0));

        outer.Merge.ShouldBe(outer.BranchEntry);
        outer.HasElse.ShouldBeFalse();
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (outer)",
            "{",
            "    if (early)",
            "    {",
            "        return 1;",
            "    }",
            "    y = 1;",
            "}",
            "if (tail)",
            "{",
            "    return 2;",
            "}",
            "if (other)",
            "{",
            "    return 3;",
            "}",
            "else",
            "{",
            "    return 4;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Region_analysis_recovers_partial_merge_with_terminating_subpaths()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("choice")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.String("earlyA")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionReturn(),
            new ActionPush([PushValue.String("a")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionJump(0),
            new ActionPush([PushValue.String("earlyB")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionReturn(),
            new ActionPush([PushValue.String("b")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Integer(3)]),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 2, offsets[13]);
        SetBranch(actions, index: 6, offsets[9]);
        SetBranch(actions, index: 12, offsets[22]);
        SetBranch(actions, index: 16, offsets[19]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var outer = result.RegionAnalysis.IfRegions.Single(region => region.Header == new BlockIndex(0));
        var text = result.GetStructuredAs2Text();

        outer.Merge.IsValid.ShouldBeTrue();
        outer.Merge.ShouldNotBe(outer.FallThroughEntry);
        outer.Merge.ShouldNotBe(outer.BranchEntry);
        outer.HasElse.ShouldBeTrue();
        text.ShouldContain("a = 1;");
        text.ShouldContain("b = 2;");
        Regex.Matches(text, @"(?m)^[ \t]*return [123];\r?$").Count.ShouldBe(3);
    }

    [Fact]
    public void Region_analysis_preserves_terminal_if_else_with_unreachable_jump_separator()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("flag")]),
            new ActionGetVariable(),
            new ActionIf(0),
            new ActionPush([PushValue.Integer(1)]),
            new ActionReturn(),
            new ActionJump(0),
            new ActionPush([PushValue.Integer(2)]),
            new ActionReturn()
        };

        var offsets = ComputeOffsets(actions);
        var endOffset = offsets[^1] + Len(actions[^1]);
        SetBranch(actions, index: 2, offsets[6]);
        SetBranch(actions, index: 5, endOffset);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);
        var region = result.RegionAnalysis.IfRegions.Single(region => region.Header == new BlockIndex(0));

        region.Merge.ShouldBe(BlockIndex.Invalid);
        region.HasElse.ShouldBeTrue();
        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (!flag)",
            "{",
            "    return 1;",
            "}",
            "else",
            "{",
            "    return 2;",
            "}",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_keeps_nested_if_inside_early_return_guard()
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("outer")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("inner")]),
            new ActionGetVariable(),
            new ActionNot(),
            new ActionIf(0),
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable(),
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn(),
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var offsets = ComputeOffsets(actions);
        SetBranch(actions, index: 3, offsets[13]);
        SetBranch(actions, index: 7, offsets[11]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (outer)",
            "{",
            "    if (inner)",
            "    {",
            "        y = 1;",
            "    }",
            "    return;",
            "}",
            "z = 2;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_prints_if_else_assignment_blocks()
    {
        var thenBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionSetVariable()
        };

        var elseBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("y")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionSetVariable()
        };

        var mergeBody = new Avm1Action[]
        {
            new ActionPush([PushValue.String("z")]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionSetVariable()
        };

        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String("x")]),
            new ActionGetVariable(),
            new ActionIf(0)
        };
        actions.AddRange(thenBody);
        actions.Add(new ActionJump(0));
        actions.AddRange(elseBody);
        actions.AddRange(mergeBody);

        var offsets = ComputeOffsets(actions);
        var ifIndex = 2;
        var jumpIndex = ifIndex + 1 + thenBody.Length;
        var elseStart = jumpIndex + 1;
        var mergeStart = elseStart + elseBody.Length;

        SetBranch(actions, ifIndex, offsets[elseStart]);
        SetBranch(actions, jumpIndex, offsets[mergeStart]);

        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "if (!x)",
            "{",
            "    y = 1;",
            "}",
            "else",
            "{",
            "    y = 2;",
            "}",
            "z = 3;",
            string.Empty
        ]));
    }

    [Fact]
    public void Structured_as2_emitter_escapes_string_literals_without_raw_control_characters()
    {
        const string literal =
            "quote\" slash\\ backspace\b tab\t newline\n vertical\v formfeed\f return\r " +
            "null\0 unit\u001F delete\u007F next\u0085 line\u2028 paragraph\u2029 emoji \U0001F600";
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("value")]),
            new ActionPush([PushValue.String(literal)]),
            new ActionSetVariable()
        ], swfVersion: 6);

        result.GetStructuredAs2Text().ShouldBe(
            """value = "quote\" slash\\ backspace\b tab\t newline\n vertical\u000B formfeed\f return\r null\u0000 unit\u001F delete\u007F next\u0085 line\u2028 paragraph\u2029 emoji \uD83D\uDE00";""" +
            Environment.NewLine);
    }
}
