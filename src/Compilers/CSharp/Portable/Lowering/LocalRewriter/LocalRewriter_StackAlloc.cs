﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        public override BoundNode VisitConvertedStackAllocExpression(BoundConvertedStackAllocExpression stackAllocNode)
        {
            return VisitStackAllocArrayCreation(stackAllocNode);
        }

        public override BoundNode VisitStackAllocArrayCreation(BoundStackAllocArrayCreation stackAllocNode)
        {
            var rewrittenCount = VisitExpression(stackAllocNode.Count);
            var type = stackAllocNode.Type;

            if (rewrittenCount.ConstantValue?.Int32Value == 0)
            {
                // either default(span) or nullptr
                return _factory.Default(type);
            }

            var elementType = stackAllocNode.ElementType;

            if (type.IsPointerType())
            {
                var stackSize = RewriteStackAllocCountToSize(rewrittenCount, elementType);
                return new BoundConvertedStackAllocExpression(stackAllocNode.Syntax, elementType, stackSize, stackAllocNode.Type);
            }
            else if (type.OriginalDefinition == _compilation.GetWellKnownType(WellKnownType.System_Span_T))
            {
                var spanType = (NamedTypeSymbol)stackAllocNode.Type;
                var sideEffects = ArrayBuilder<BoundExpression>.GetInstance();
                var locals = ArrayBuilder<LocalSymbol>.GetInstance();
                var countTemp = CaptureExpressionInTempIfNeeded(rewrittenCount, sideEffects, locals);
                var stackSize = RewriteStackAllocCountToSize(countTemp, elementType);
                stackAllocNode = new BoundConvertedStackAllocExpression(stackAllocNode.Syntax, elementType, stackSize, spanType);

                BoundExpression constructorCall;
                if (TryGetWellKnownTypeMember(stackAllocNode.Syntax, WellKnownMember.System_Span_T__ctor, out MethodSymbol spanConstructor))
                {
                    constructorCall = _factory.New((MethodSymbol)spanConstructor.SymbolAsMember(spanType), stackAllocNode, countTemp);
                }
                else
                {
                    constructorCall = new BoundBadExpression(
                        syntax: stackAllocNode.Syntax,
                        resultKind: LookupResultKind.NotInvocable,
                        symbols: ImmutableArray<Symbol>.Empty,
                        childBoundNodes: ImmutableArray<BoundExpression>.Empty,
                        type: ErrorTypeSymbol.UnknownResultType);
                }

                return new BoundSequence(
                    syntax: stackAllocNode.Syntax,
                    locals: ImmutableArray.Create(countTemp.LocalSymbol),
                    sideEffects: ImmutableArray.Create<BoundExpression>(countTempAssignment),
                    value: constructorCall,
                    type: spanType);
            }
            else
            {
                throw ExceptionUtilities.UnexpectedValue(type);
            }
        }

        private BoundExpression RewriteStackAllocCountToSize(BoundExpression countExpression, TypeSymbol elementType)
        {
            // From ILGENREC::genExpr:
            // EDMAURER always perform a checked multiply regardless of the context.
            // localloc takes an unsigned native int. When a user specifies a negative
            // count of elements, per spec, the behavior is undefined. So convert element
            // count to unsigned.

            // NOTE: to match this special case logic, we're going to construct the multiplication
            // ourselves, rather than calling MakeSizeOfMultiplication (which inserts various checks 
            // and conversions).

            TypeSymbol uintType = _factory.SpecialType(SpecialType.System_UInt32);
            TypeSymbol uintPtrType = _factory.SpecialType(SpecialType.System_UIntPtr);

            // Why convert twice?  Because dev10 actually uses an explicit conv_u instruction and the normal conversion
            // from int32 to native uint is emitted as conv_i.  The behavior we want to emulate is to re-interpret
            // (i.e. unchecked) an int32 as unsigned (i.e. uint32) and then convert it to a native uint *without* sign
            // extension.

            BoundExpression sizeOfExpression = _factory.Sizeof(elementType);

            var sizeConst = sizeOfExpression.ConstantValue;
            if (sizeConst != null)
            {
                int size = sizeConst.Int32Value;
                Debug.Assert(size > 0);

                // common case: stackalloc int[123]
                var countConst = countExpression.ConstantValue;
                if (countConst != null)
                {
                    var count = countConst.Int32Value;
                    long folded = unchecked((uint)count * size);

                    if (folded < uint.MaxValue)
                    {
                        return _factory.Convert(uintPtrType, _factory.Literal((uint)folded), Conversion.IntegerToPointer);
                    }
                }
            }
            
            BoundExpression convertedCount = _factory.Convert(uintType, countExpression, Conversion.ExplicitNumeric);
            convertedCount = _factory.Convert(uintPtrType, convertedCount, Conversion.IntegerToPointer);

            // another common case: stackalloc byte[x]
            if (sizeConst?.Int32Value == 1)
            {
                return convertedCount;
            }

            BinaryOperatorKind multiplicationKind = BinaryOperatorKind.Checked | BinaryOperatorKind.UIntMultiplication; //"UInt" just to make it unsigned
            BoundExpression product = _factory.Binary(multiplicationKind, uintPtrType, convertedCount, sizeOfExpression);

            return product;
        }
    }
}
