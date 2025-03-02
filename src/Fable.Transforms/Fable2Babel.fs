module rec Fable.Transforms.Fable2Babel

open Fable
open Fable.AST
open Fable.AST.Babel
open System.Collections.Generic

type ReturnStrategy =
    | Return
    | ReturnUnit
    | Assign of Expression
    | Target of Identifier

type ArgsInfo =
    | CallInfo of Fable.CallInfo
    | NoCallInfo of args: Fable.Expr list

type Import =
  { Selector: string
    LocalIdent: string option
    Path: string }

type ITailCallOpportunity =
    abstract Label: string
    abstract Args: string list
    abstract IsRecursiveRef: Fable.Expr -> bool

type UsedNames =
  { RootScope: HashSet<string>
    DeclarationScopes: HashSet<string>
    CurrentDeclarationScope: HashSet<string> }

type Context =
  { File: Fable.File
    UsedNames: UsedNames
    DecisionTargets: (Fable.Ident list * Fable.Expr) list
    HoistVars: Fable.Ident list -> bool
    TailCallOpportunity: ITailCallOpportunity option
    OptimizeTailCall: unit -> unit
    ScopedTypeParams: Set<string> }

type IBabelCompiler =
    inherit Compiler
    abstract GetAllImports: unit -> seq<Import>
    abstract GetImportExpr: Context * selector: string * path: string * SourceLocation option -> Expression
    abstract TransformAsExpr: Context * Fable.Expr -> Expression
    abstract TransformAsStatements: Context * ReturnStrategy option * Fable.Expr -> Statement array
    abstract TransformImport: Context * selector:string * path:string -> Expression
    abstract TransformFunction: Context * string option * Fable.Ident list * Fable.Expr -> (Pattern array) * BlockStatement
    abstract WarnOnlyOnce: string * ?range: SourceLocation -> unit

// TODO: All things that depend on the library should be moved to Replacements
// to become independent of the specific implementation
module Lib =
    let libCall (com: IBabelCompiler) ctx r moduleName memberName args =
        Expression.callExpression(com.TransformImport(ctx, memberName, getLibPath com moduleName), args, ?loc=r)

    let libConsCall (com: IBabelCompiler) ctx r moduleName memberName args =
        Expression.newExpression(com.TransformImport(ctx, memberName, getLibPath com moduleName), args, ?loc=r)

    let libValue (com: IBabelCompiler) ctx moduleName memberName =
        com.TransformImport(ctx, memberName, getLibPath com moduleName)

    let tryJsConstructor (com: IBabelCompiler) ctx ent =
        match Replacements.tryJsConstructor com ent with
        | Some e -> com.TransformAsExpr(ctx, e) |> Some
        | None -> None

    let jsConstructor (com: IBabelCompiler) ctx ent =
        let entRef = Replacements.jsConstructor com ent
        com.TransformAsExpr(ctx, entRef)

// TODO: This is too implementation-dependent, ideally move it to Replacements
module Reflection =
    open Lib

    let private libReflectionCall (com: IBabelCompiler) ctx r memberName args =
        libCall com ctx r "Reflection" (memberName + "_type") args

    let private transformRecordReflectionInfo com ctx r (ent: Fable.Entity) generics =
        // TODO: Refactor these three bindings to reuse in transformUnionReflectionInfo
        let fullname = ent.FullName
        let fullnameExpr = Expression.stringLiteral(fullname)
        let genMap =
            let genParamNames = ent.GenericParameters |> List.mapToArray (fun x -> x.Name) |> Seq.toArray
            Array.zip genParamNames generics |> Map |> Some
        let fields =
            ent.FSharpFields |> Seq.map (fun fi ->
                let typeInfo = transformTypeInfo com ctx r genMap fi.FieldType
                (Expression.arrayExpression([|Expression.stringLiteral(fi.Name); typeInfo|])))
            |> Seq.toArray
        let fields = Expression.arrowFunctionExpression([||], Expression.arrayExpression(fields))
        [|fullnameExpr; Expression.arrayExpression(generics); jsConstructor com ctx ent; fields|]
        |> libReflectionCall com ctx None "record"

    let private transformUnionReflectionInfo com ctx r (ent: Fable.Entity) generics =
        let fullname = ent.FullName
        let fullnameExpr = Expression.stringLiteral(fullname)
        let genMap =
            let genParamNames = ent.GenericParameters |> List.map (fun x -> x.Name) |> Seq.toArray
            Array.zip genParamNames generics |> Map |> Some
        let cases =
            ent.UnionCases |> Seq.map (fun uci ->
                uci.UnionCaseFields |> List.mapToArray (fun fi ->
                    Expression.arrayExpression([|
                        fi.Name |> Expression.stringLiteral
                        transformTypeInfo com ctx r genMap fi.FieldType
                    |]))
                |> Expression.arrayExpression
            ) |> Seq.toArray
        let cases = Expression.arrowFunctionExpression([||], Expression.arrayExpression(cases))
        [|fullnameExpr; Expression.arrayExpression(generics); jsConstructor com ctx ent; cases|]
        |> libReflectionCall com ctx None "union"

    let transformTypeInfo (com: IBabelCompiler) ctx r (genMap: Map<string, Expression> option) t: Expression =
        let primitiveTypeInfo name =
           libValue com ctx "Reflection" (name + "_type")
        let numberInfo kind =
            getNumberKindName kind
            |> primitiveTypeInfo
        let nonGenericTypeInfo fullname =
            [| Expression.stringLiteral(fullname) |]
            |> libReflectionCall com ctx None "class"
        let resolveGenerics generics: Expression[] =
            generics |> Array.map (transformTypeInfo com ctx r genMap)
        let genericTypeInfo name genArgs =
            let resolved = resolveGenerics genArgs
            libReflectionCall com ctx None name resolved
        let genericEntity (fullname: string) generics =
            libReflectionCall com ctx None "class" [|
                Expression.stringLiteral(fullname)
                if not(Array.isEmpty generics) then
                    Expression.arrayExpression(generics)
            |]
        let genericGlobalOrImportedEntity generics (ent: Fable.Entity) =
            libReflectionCall com ctx None "class" [|
                yield Expression.stringLiteral(ent.FullName)
                match generics with
                | [||] -> yield Util.undefined None
                | generics -> yield Expression.arrayExpression(generics)
                match tryJsConstructor com ctx ent with
                | Some cons -> yield cons
                | None -> ()
            |]
        match t with
        | Fable.Any -> primitiveTypeInfo "obj"
        | Fable.GenericParam name ->
            match genMap with
            | None -> [| Expression.stringLiteral(name) |] |> libReflectionCall com ctx None "generic"
            | Some genMap ->
                match Map.tryFind name genMap with
                | Some t -> t
                | None ->
                    Replacements.genericTypeInfoError name |> addError com [] r
                    Expression.nullLiteral()
        | Fable.Unit    -> primitiveTypeInfo "unit"
        | Fable.Boolean -> primitiveTypeInfo "bool"
        | Fable.Char    -> primitiveTypeInfo "char"
        | Fable.String  -> primitiveTypeInfo "string"
        | Fable.Enum entRef ->
            let ent = com.GetEntity(entRef)
            let mutable numberKind = Int32
            let cases =
                ent.FSharpFields |> Seq.choose (fun fi ->
                    // F# seems to include a field with this name in the underlying type
                    match fi.Name with
                    | "value__" ->
                        match fi.FieldType with
                        | Fable.Number kind -> numberKind <- kind
                        | _ -> ()
                        None
                    | name ->
                        let value = match fi.LiteralValue with Some v -> System.Convert.ToDouble v | None -> 0.
                        Expression.arrayExpression([|Expression.stringLiteral(name); Expression.numericLiteral(value)|]) |> Some)
                |> Seq.toArray
                |> Expression.arrayExpression
            [|Expression.stringLiteral(entRef.FullName); numberInfo numberKind; cases |]
            |> libReflectionCall com ctx None "enum"
        | Fable.Number kind ->
            numberInfo kind
        | Fable.LambdaType(argType, returnType) ->
            genericTypeInfo "lambda" [|argType; returnType|]
        | Fable.DelegateType(argTypes, returnType) ->
            genericTypeInfo "delegate" ([|yield! argTypes; yield returnType|])
        | Fable.Tuple genArgs   -> genericTypeInfo "tuple" (List.toArray genArgs)
        | Fable.Option genArg   -> genericTypeInfo "option" [|genArg|]
        | Fable.Array genArg    -> genericTypeInfo "array" [|genArg|]
        | Fable.List genArg     -> genericTypeInfo "list" [|genArg|]
        | Fable.Regex           -> nonGenericTypeInfo Types.regex
        | Fable.MetaType        -> nonGenericTypeInfo Types.type_
        | Fable.AnonymousRecordType(fieldNames, genArgs) ->
            let genArgs = resolveGenerics (List.toArray genArgs)
            Array.zip fieldNames genArgs
            |> Array.map (fun (k, t) -> Expression.arrayExpression[|Expression.stringLiteral(k); t|])
            |> libReflectionCall com ctx None "anonRecord"
        | Fable.DeclaredType(entRef, generics) ->
            let fullName = entRef.FullName
            match fullName, generics with
            | Replacements.BuiltinEntity kind ->
                match kind with
                | Replacements.BclGuid
                | Replacements.BclTimeSpan
                | Replacements.BclDateTime
                | Replacements.BclDateTimeOffset
                | Replacements.BclDateOnly
                | Replacements.BclTimeOnly
                | Replacements.BclTimer
                | Replacements.BclInt64
                | Replacements.BclUInt64
                | Replacements.BclDecimal
                | Replacements.BclBigInt -> genericEntity fullName [||]
                | Replacements.BclHashSet gen
                | Replacements.FSharpSet gen ->
                    genericEntity fullName [|transformTypeInfo com ctx r genMap gen|]
                | Replacements.BclDictionary(key, value)
                | Replacements.BclKeyValuePair(key, value)
                | Replacements.FSharpMap(key, value) ->
                    genericEntity fullName [|
                        transformTypeInfo com ctx r genMap key
                        transformTypeInfo com ctx r genMap value
                    |]
                | Replacements.FSharpResult(ok, err) ->
                    let ent = com.GetEntity(entRef)
                    transformUnionReflectionInfo com ctx r ent [|
                        transformTypeInfo com ctx r genMap ok
                        transformTypeInfo com ctx r genMap err
                    |]
                | Replacements.FSharpChoice gen ->
                    let ent = com.GetEntity(entRef)
                    let gen = List.map (transformTypeInfo com ctx r genMap) gen
                    List.toArray gen |> transformUnionReflectionInfo com ctx r ent
                | Replacements.FSharpReference gen ->
                    let ent = com.GetEntity(entRef)
                    [|transformTypeInfo com ctx r genMap gen|]
                    |> transformRecordReflectionInfo com ctx r ent
            | _ ->
                let ent = com.GetEntity(entRef)
                let generics = generics |> List.map (transformTypeInfo com ctx r genMap) |> List.toArray
                // Check if the entity is actually declared in JS code
                // TODO: Interfaces should be declared when generating Typescript
                if FSharp2Fable.Util.isGlobalOrImportedEntity ent then
                    genericGlobalOrImportedEntity generics ent
                elif ent.IsInterface
                    || FSharp2Fable.Util.isErasedOrStringEnumEntity ent
                    || FSharp2Fable.Util.isReplacementCandidate ent then
                    genericEntity ent.FullName generics
                // TODO: Strictly measure types shouldn't appear in the runtime, but we support it for now
                // See Fable.Transforms.FSharp2Fable.TypeHelpers.makeTypeGenArgs
                elif ent.IsMeasure then
                    [| Expression.stringLiteral(ent.FullName) |]
                    |> libReflectionCall com ctx None "measure"
                else
                    let reflectionMethodExpr = FSharp2Fable.Util.entityRefWithSuffix com ent Naming.reflectionSuffix
                    let callee = com.TransformAsExpr(ctx, reflectionMethodExpr)
                    Expression.callExpression(callee, generics)

    let transformReflectionInfo com ctx r (ent: Fable.Entity) generics =
        if ent.IsFSharpRecord then
            transformRecordReflectionInfo com ctx r ent generics
        elif ent.IsFSharpUnion then
            transformUnionReflectionInfo com ctx r ent generics
        else
            let fullname = ent.FullName
            [|
                yield Expression.stringLiteral(fullname)
                match generics with
                | [||] -> yield Util.undefined None
                | generics -> yield Expression.arrayExpression(generics)
                match tryJsConstructor com ctx ent with
                | Some cons -> yield cons
                | None -> ()
                match ent.BaseType with
                | Some d ->
                    let genMap =
                        Seq.zip ent.GenericParameters generics
                        |> Seq.map (fun (p, e) -> p.Name, e)
                        |> Map
                        |> Some
                    yield Fable.DeclaredType(d.Entity, d.GenericArgs)
                          |> transformTypeInfo com ctx r genMap
                | None -> ()
            |]
            |> libReflectionCall com ctx r "class"

    let private ofString s = Expression.stringLiteral(s)
    let private ofArray babelExprs = Expression.arrayExpression(List.toArray babelExprs)

    let transformTypeTest (com: IBabelCompiler) ctx range expr (typ: Fable.Type): Expression =
        let warnAndEvalToFalse msg =
            "Cannot type test (evals to false): " + msg
            |> addWarning com [] range
            Expression.booleanLiteral(false)

        let jsTypeof (primitiveType: string) (Util.TransformExpr com ctx expr): Expression =
            let typeof = Expression.unaryExpression(UnaryTypeof, expr)
            Expression.binaryExpression(BinaryEqualStrict, typeof, Expression.stringLiteral(primitiveType), ?loc=range)

        let jsInstanceof consExpr (Util.TransformExpr com ctx expr): Expression =
            Expression.binaryExpression(BinaryInstanceOf, expr, consExpr, ?loc=range)

        match typ with
        | Fable.Any -> Expression.booleanLiteral(true)
        | Fable.Unit -> Expression.binaryExpression(BinaryEqual, com.TransformAsExpr(ctx, expr), Util.undefined None, ?loc=range)
        | Fable.Boolean -> jsTypeof "boolean" expr
        | Fable.Char | Fable.String _ -> jsTypeof "string" expr
        | Fable.Number _ | Fable.Enum _ -> jsTypeof "number" expr
        | Fable.Regex -> jsInstanceof (Expression.identifier("RegExp")) expr
        | Fable.LambdaType _ | Fable.DelegateType _ -> jsTypeof "function" expr
        | Fable.Array _ | Fable.Tuple _ ->
            libCall com ctx None "Util" "isArrayLike" [|com.TransformAsExpr(ctx, expr)|]
        | Fable.List _ ->
            jsInstanceof (libValue com ctx "List" "FSharpList") expr
        | Fable.AnonymousRecordType _ ->
            warnAndEvalToFalse "anonymous records"
        | Fable.MetaType ->
            jsInstanceof (libValue com ctx "Reflection" "TypeInfo") expr
        | Fable.Option _ -> warnAndEvalToFalse "options" // TODO
        | Fable.GenericParam _ -> warnAndEvalToFalse "generic parameters"
        | Fable.DeclaredType (ent, genArgs) ->
            match ent.FullName with
            | Types.idisposable ->
                match expr with
                | MaybeCasted(ExprType(Fable.DeclaredType (ent2, _)))
                        when com.GetEntity(ent2) |> FSharp2Fable.Util.hasInterface Types.idisposable ->
                    Expression.booleanLiteral(true)
                | _ -> libCall com ctx None "Util" "isDisposable" [|com.TransformAsExpr(ctx, expr)|]
            | Types.ienumerable ->
                [|com.TransformAsExpr(ctx, expr)|]
                |> libCall com ctx None "Util" "isIterable"
            | Types.array ->
                [|com.TransformAsExpr(ctx, expr)|]
                |> libCall com ctx None "Util" "isArrayLike"
            | Types.exception_ ->
                [|com.TransformAsExpr(ctx, expr)|]
                |> libCall com ctx None "Types" "isException"
            | _ ->
                let ent = com.GetEntity(ent)
                if ent.IsInterface then
                    match FSharp2Fable.Util.tryGlobalOrImportedEntity com ent with
                    | Some typeExpr ->
                        let typeExpr = com.TransformAsExpr(ctx, typeExpr)
                        jsInstanceof typeExpr expr
                    | None -> warnAndEvalToFalse "interfaces"
                else
                    match tryJsConstructor com ctx ent with
                    | Some cons ->
                        if not(List.isEmpty genArgs) then
                            com.WarnOnlyOnce("Generic args are ignored in type testing", ?range=range)
                        jsInstanceof cons expr
                    | None ->
                        warnAndEvalToFalse ent.FullName

// TODO: I'm trying to tell apart the code to generate annotations, but it's not a very clear distinction
// as there are many dependencies from/to the Util module below
module Annotation =
    let getEntityGenParams (ent: Fable.Entity) =
        ent.GenericParameters
        |> Seq.map (fun x -> x.Name)
        |> Set.ofSeq

    let makeTypeParamDecl genParams =
        if (Set.isEmpty genParams) then
            None
        else
            genParams
            |> Set.toArray
            |> Array.map TypeParameter.typeParameter
            |> TypeParameterDeclaration |> Some

    let makeTypeParamInst genParams =
        if (Set.isEmpty genParams) then
            None
        else
            genParams
            |> Set.toArray
            |> Array.map (Identifier.identifier >> TypeAnnotationInfo.genericTypeAnnotation)
            |> TypeParameterInstantiation |> Some

    let mergeTypeParamDecls (decl1: TypeParameterDeclaration option) (decl2: TypeParameterDeclaration option) =
        match decl1, decl2 with
        | Some (TypeParameterDeclaration(parameters=p1)), Some (TypeParameterDeclaration(parameters=p2)) ->
            Array.append
                (p1 |> Array.map (fun (TypeParameter(name=name)) -> name))
                (p2 |> Array.map (fun (TypeParameter(name=name)) -> name))
            |> Array.distinct
            |> Array.map TypeParameter.typeParameter
            |> TypeParameterDeclaration |> Some
        | Some _, None -> decl1
        | None, Some _ -> decl2
        | None, None -> None

    let getGenericTypeAnnotation _com _ctx name genParams =
        let typeParamInst = makeTypeParamInst genParams
        GenericTypeAnnotation(Identifier.identifier(name), ?typeParameters=typeParamInst)
        |> TypeAnnotation |> Some

    let typeAnnotation com ctx typ: TypeAnnotationInfo =
        match typ with
        | Fable.MetaType -> AnyTypeAnnotation
        | Fable.Any -> AnyTypeAnnotation
        | Fable.Unit -> VoidTypeAnnotation
        | Fable.Boolean -> BooleanTypeAnnotation
        | Fable.Char -> StringTypeAnnotation
        | Fable.String -> StringTypeAnnotation
        | Fable.Regex -> AnyTypeAnnotation
        | Fable.Number kind -> makeNumericTypeAnnotation com ctx kind
        | Fable.Enum _ent -> NumberTypeAnnotation
        | Fable.Option genArg -> makeOptionTypeAnnotation com ctx genArg
        | Fable.Tuple genArgs -> makeTupleTypeAnnotation com ctx genArgs
        | Fable.Array genArg -> makeArrayTypeAnnotation com ctx genArg
        | Fable.List genArg -> makeListTypeAnnotation com ctx genArg
        | Replacements.Builtin kind -> makeBuiltinTypeAnnotation com ctx kind
        | Fable.LambdaType _ -> Util.uncurryLambdaType typ ||> makeFunctionTypeAnnotation com ctx typ
        | Fable.DelegateType(argTypes, returnType) -> makeFunctionTypeAnnotation com ctx typ argTypes returnType
        | Fable.GenericParam name -> makeSimpleTypeAnnotation com ctx name
        | Fable.DeclaredType(ent, genArgs) ->
            makeEntityTypeAnnotation com ctx ent genArgs
        | Fable.AnonymousRecordType(fieldNames, genArgs) ->
            makeAnonymousRecordTypeAnnotation com ctx fieldNames genArgs

    let makeSimpleTypeAnnotation _com _ctx name =
        TypeAnnotationInfo.genericTypeAnnotation(Identifier.identifier(name))

    let makeGenTypeParamInst com ctx genArgs =
        match genArgs with
        | [] -> None
        | _  -> genArgs |> List.map (typeAnnotation com ctx)
                        |> List.toArray |> TypeParameterInstantiation |> Some

    let makeGenericTypeAnnotation com ctx genArgs id =
        let typeParamInst = makeGenTypeParamInst com ctx genArgs
        GenericTypeAnnotation(id, ?typeParameters=typeParamInst)

    let makeNativeTypeAnnotation com ctx genArgs typeName =
        Identifier.identifier(typeName)
        |> makeGenericTypeAnnotation com ctx genArgs

    let makeImportTypeId (com: IBabelCompiler) ctx moduleName typeName =
        let expr = com.GetImportExpr(ctx, typeName, getLibPath com moduleName, None)
        match expr with
        | Expression.Identifier(id) -> id
        | _ -> Identifier.identifier(typeName)

    let makeImportTypeAnnotation com ctx genArgs moduleName typeName =
        let id = makeImportTypeId com ctx moduleName typeName
        makeGenericTypeAnnotation com ctx genArgs id

    let makeNumericTypeAnnotation com ctx kind =
        let typeName = getNumberKindName kind
        makeImportTypeAnnotation com ctx [] "Int32" typeName

    let makeOptionTypeAnnotation com ctx genArg =
        makeImportTypeAnnotation com ctx [genArg] "Option" "Option"

    let makeTupleTypeAnnotation com ctx genArgs =
        List.map (typeAnnotation com ctx) genArgs
        |> List.toArray |> TupleTypeAnnotation

    let makeArrayTypeAnnotation com ctx genArg =
        match genArg with
        | Fable.Number kind when com.Options.TypedArrays ->
            let name = getTypedArrayName com kind
            makeSimpleTypeAnnotation com ctx name
        | _ ->
            makeNativeTypeAnnotation com ctx [genArg] "Array"

    let makeListTypeAnnotation com ctx genArg =
        makeImportTypeAnnotation com ctx [genArg] "List" "List"

    let makeUnionTypeAnnotation com ctx genArgs =
        List.map (typeAnnotation com ctx) genArgs
        |> List.toArray |> UnionTypeAnnotation

    let makeBuiltinTypeAnnotation com ctx kind =
        match kind with
        | Replacements.BclGuid -> StringTypeAnnotation
        | Replacements.BclTimeSpan -> NumberTypeAnnotation
        | Replacements.BclDateTime -> makeSimpleTypeAnnotation com ctx "Date"
        | Replacements.BclDateTimeOffset -> makeSimpleTypeAnnotation com ctx "Date"
        | Replacements.BclDateOnly -> makeSimpleTypeAnnotation com ctx "Date"
        | Replacements.BclTimeOnly -> NumberTypeAnnotation
        | Replacements.BclTimer -> makeImportTypeAnnotation com ctx [] "Timer" "Timer"
        | Replacements.BclInt64 -> makeImportTypeAnnotation com ctx [] "Long" "int64"
        | Replacements.BclUInt64 -> makeImportTypeAnnotation com ctx [] "Long" "uint64"
        | Replacements.BclDecimal -> makeImportTypeAnnotation com ctx [] "Decimal" "decimal"
        | Replacements.BclBigInt -> makeImportTypeAnnotation com ctx [] "BigInt/z" "BigInteger"
        | Replacements.BclHashSet key -> makeNativeTypeAnnotation com ctx [key] "Set"
        | Replacements.BclDictionary (key, value) -> makeNativeTypeAnnotation com ctx [key; value] "Map"
        | Replacements.BclKeyValuePair (key, value) -> makeTupleTypeAnnotation com ctx [key; value]
        | Replacements.FSharpSet key -> makeImportTypeAnnotation com ctx [key] "Set" "FSharpSet"
        | Replacements.FSharpMap (key, value) -> makeImportTypeAnnotation com ctx [key; value] "Map" "FSharpMap"
        | Replacements.FSharpResult (ok, err) -> makeImportTypeAnnotation com ctx [ok; err] "Fable.Core" "FSharpResult$2"
        | Replacements.FSharpChoice genArgs ->
            $"FSharpChoice${List.length genArgs}"
            |> makeImportTypeAnnotation com ctx genArgs "Fable.Core"
        | Replacements.FSharpReference genArg -> makeImportTypeAnnotation com ctx [genArg] "Types" "FSharpRef"

    let makeFunctionTypeAnnotation com ctx _typ argTypes returnType =
        let funcTypeParams =
            argTypes
            |> List.mapi (fun i argType ->
                FunctionTypeParam.functionTypeParam(
                    Identifier.identifier("arg" + (string i)),
                    typeAnnotation com ctx argType))
            |> List.toArray
        let genTypeParams = Util.getGenericTypeParams (argTypes @ [returnType])
        let newTypeParams = Set.difference genTypeParams ctx.ScopedTypeParams
        let ctx = { ctx with ScopedTypeParams = Set.union ctx.ScopedTypeParams newTypeParams }
        let returnType = typeAnnotation com ctx returnType
        let typeParamDecl = makeTypeParamDecl newTypeParams
        TypeAnnotationInfo.functionTypeAnnotation(funcTypeParams, returnType, ?typeParameters=typeParamDecl)

    let makeEntityTypeAnnotation com ctx (ent: Fable.EntityRef) genArgs =
        match ent.FullName with
        | Types.ienumerableGeneric ->
            makeNativeTypeAnnotation com ctx genArgs "IterableIterator"
        | Types.result ->
            makeUnionTypeAnnotation com ctx genArgs
        | entName when entName.StartsWith(Types.choiceNonGeneric) ->
            makeUnionTypeAnnotation com ctx genArgs
        | _ ->
            let ent = com.GetEntity(ent)
            if ent.IsInterface then
                AnyTypeAnnotation // TODO:
            else
                match Lib.tryJsConstructor com ctx ent with
                | Some entRef ->
                    match entRef with
                    | Literal(Literal.StringLiteral(StringLiteral(str, _))) ->
                        match str with
                        | "number" -> NumberTypeAnnotation
                        | "boolean" -> BooleanTypeAnnotation
                        | "string" -> StringTypeAnnotation
                        | _ -> AnyTypeAnnotation
                    | Expression.Identifier(id) ->
                        makeGenericTypeAnnotation com ctx genArgs id
                    // TODO: Resolve references to types in nested modules
                    | _ -> AnyTypeAnnotation
                | None -> AnyTypeAnnotation

    let makeAnonymousRecordTypeAnnotation _com _ctx _fieldNames _genArgs =
         AnyTypeAnnotation // TODO:

    let typedIdent (com: IBabelCompiler) ctx (id: Fable.Ident) =
        if com.Options.Language = TypeScript then
            let ta = typeAnnotation com ctx id.Type |> TypeAnnotation |> Some
            let optional = None // match id.Type with | Fable.Option _ -> Some true | _ -> None
            Identifier.identifier(id.Name, ?optional=optional, ?typeAnnotation=ta, ?loc=id.Range)
        else
            Identifier.identifier(id.Name, ?loc=id.Range)

    let transformFunctionWithAnnotations (com: IBabelCompiler) ctx name (args: Fable.Ident list) (body: Fable.Expr) =
        if com.Options.Language = TypeScript then
            let argTypes = args |> List.map (fun id -> id.Type)
            let genTypeParams = Util.getGenericTypeParams (argTypes @ [body.Type])
            let newTypeParams = Set.difference genTypeParams ctx.ScopedTypeParams
            let ctx = { ctx with ScopedTypeParams = Set.union ctx.ScopedTypeParams newTypeParams }
            let args', body' = com.TransformFunction(ctx, name, args, body)
            let returnType = TypeAnnotation(typeAnnotation com ctx body.Type) |> Some
            let typeParamDecl = makeTypeParamDecl newTypeParams
            args', body', returnType, typeParamDecl
        else
            let args', body' = com.TransformFunction(ctx, name, args, body)
            args', body', None, None

module Util =
    open Lib
    open Reflection
    open Annotation

    let (|TransformExpr|) (com: IBabelCompiler) ctx e =
        com.TransformAsExpr(ctx, e)

    let (|Function|_|) = function
        | Fable.Lambda(arg, body, _) -> Some([arg], body)
        | Fable.Delegate(args, body, _) -> Some(args, body)
        | _ -> None

    let (|Lets|_|) = function
        | Fable.Let(ident, value, body) -> Some([ident, value], body)
        | Fable.LetRec(bindings, body) -> Some(bindings, body)
        | _ -> None

    let discardUnitArg (args: Fable.Ident list) =
        match args with
        | [] -> []
        | [unitArg] when unitArg.Type = Fable.Unit -> []
        | [thisArg; unitArg] when thisArg.IsThisArgument && unitArg.Type = Fable.Unit -> [thisArg]
        | args -> args

    let getUniqueNameInRootScope (ctx: Context) name =
        let name = (name, Naming.NoMemberPart) ||> Naming.sanitizeIdent (fun name ->
            ctx.UsedNames.RootScope.Contains(name)
            || ctx.UsedNames.DeclarationScopes.Contains(name))
        ctx.UsedNames.RootScope.Add(name) |> ignore
        name

    let getUniqueNameInDeclarationScope (ctx: Context) name =
        let name = (name, Naming.NoMemberPart) ||> Naming.sanitizeIdent (fun name ->
            ctx.UsedNames.RootScope.Contains(name) || ctx.UsedNames.CurrentDeclarationScope.Contains(name))
        ctx.UsedNames.CurrentDeclarationScope.Add(name) |> ignore
        name

    type NamedTailCallOpportunity(_com: Compiler, ctx, name, args: Fable.Ident list) =
        // Capture the current argument values to prevent delayed references from getting corrupted,
        // for that we use block-scoped ES2015 variable declarations. See #681, #1859
        // TODO: Local unique ident names
        let argIds = discardUnitArg args |> List.map (fun arg ->
            getUniqueNameInDeclarationScope ctx (arg.Name + "_mut"))
        interface ITailCallOpportunity with
            member _.Label = name
            member _.Args = argIds
            member _.IsRecursiveRef(e) =
                match e with Fable.IdentExpr id -> name = id.Name | _ -> false

    let getDecisionTarget (ctx: Context) targetIndex =
        match List.tryItem targetIndex ctx.DecisionTargets with
        | None -> failwith $"Cannot find DecisionTree target %i{targetIndex}"
        | Some(idents, target) -> idents, target

    let rec isJsStatement ctx preferStatement (expr: Fable.Expr) =
        match expr with
        | Fable.Unresolved _
        | Fable.Value _ | Fable.Import _  | Fable.IdentExpr _
        | Fable.Lambda _ | Fable.Delegate _ | Fable.ObjectExpr _
        | Fable.Call _ | Fable.CurriedApply _ | Fable.Curry _ | Fable.Operation _
        | Fable.Get _ | Fable.Test _ | Fable.TypeCast _ -> false

        | Fable.TryCatch _
        | Fable.Sequential _ | Fable.Let _ | Fable.LetRec _ | Fable.Set _
        | Fable.ForLoop _ | Fable.WhileLoop _ -> true

        // TODO: If IsJsSatement is false, still try to infer it? See #2414
        // /^\s*(break|continue|debugger|while|for|switch|if|try|let|const|var)\b/
        | Fable.Emit(i,_,_) -> i.IsJsStatement

        | Fable.DecisionTreeSuccess(targetIndex,_, _) ->
            getDecisionTarget ctx targetIndex
            |> snd |> isJsStatement ctx preferStatement

        // Make it also statement if we have more than, say, 3 targets?
        // That would increase the chances to convert it into a switch
        | Fable.DecisionTree(_,targets) ->
            preferStatement
            || List.exists (snd >> (isJsStatement ctx false)) targets

        | Fable.IfThenElse(_,thenExpr,elseExpr,_) ->
            preferStatement || isJsStatement ctx false thenExpr || isJsStatement ctx false elseExpr


    let addErrorAndReturnNull (com: Compiler) (range: SourceLocation option) (error: string) =
        addError com [] range error
        Expression.nullLiteral()

    let ident (id: Fable.Ident) =
        Identifier.identifier(id.Name, ?loc=id.Range)

    let identAsExpr (id: Fable.Ident) =
        Expression.identifier(id.Name, ?loc=id.Range)

    let identAsPattern (id: Fable.Ident) =
        Pattern.identifier(id.Name, ?loc=id.Range)

    let thisExpr =
        Expression.thisExpression()

    let ofInt i =
        Expression.numericLiteral(float i)

    let ofString s =
       Expression.stringLiteral(s)

    let memberFromName (memberName: string): Expression * bool =
        match memberName with
        | "ToString" -> Expression.identifier("toString"), false
        | n when n.StartsWith("Symbol.") ->
            Expression.memberExpression(Expression.identifier("Symbol"), Expression.identifier(n.[7..]), false), true
        | n when Naming.hasIdentForbiddenChars n -> Expression.stringLiteral(n), true
        | n -> Expression.identifier(n), false

    let memberFromExpr (com: IBabelCompiler) ctx memberExpr: Expression * bool =
        match memberExpr with
        | Fable.Value(Fable.StringConstant name, _) -> memberFromName name
        | e -> com.TransformAsExpr(ctx, e), true

    let get r left memberName =
        let expr, computed = memberFromName memberName
        Expression.memberExpression(left, expr, computed, ?loc=r)

    let getExpr r (object: Expression) (expr: Expression) =
        let expr, computed =
            match expr with
            | Literal(Literal.StringLiteral(StringLiteral(value, _))) -> memberFromName value
            | e -> e, true
        Expression.memberExpression(object, expr, computed, ?loc=r)

    let rec getParts (parts: string list) (expr: Expression) =
        match parts with
        | [] -> expr
        | m::ms -> get None expr m |> getParts ms

    let makeArray (com: IBabelCompiler) ctx exprs =
        List.mapToArray (fun e -> com.TransformAsExpr(ctx, e)) exprs
        |> Expression.arrayExpression

    let makeTypedArray (com: IBabelCompiler) ctx t (args: Fable.Expr list) =
        match t with
        | Fable.Number kind when com.Options.TypedArrays ->
            let jsName = getTypedArrayName com kind
            let args = [|makeArray com ctx args|]
            Expression.newExpression(Expression.identifier(jsName), args)
        | _ -> makeArray com ctx args

    let makeTypedAllocatedFrom (com: IBabelCompiler) ctx typ (fableExpr: Fable.Expr) =
        let getArrayCons t =
            match t with
            | Fable.Number kind when com.Options.TypedArrays ->
                getTypedArrayName com kind |> Expression.identifier
            | _ -> Expression.identifier("Array")

        match fableExpr with
        | ExprType(Fable.Number _) ->
            let cons = getArrayCons typ
            let expr = com.TransformAsExpr(ctx, fableExpr)
            Expression.newExpression(cons, [|expr|])
        | Replacements.ArrayOrListLiteral(exprs, _) ->
            makeTypedArray com ctx typ exprs
        | _ ->
            let cons = getArrayCons typ
            let expr = com.TransformAsExpr(ctx, fableExpr)
            Expression.callExpression(get None cons "from", [|expr|])

    let makeStringArray strings =
        strings
        |> List.mapToArray (fun x -> Expression.stringLiteral(x))
        |> Expression.arrayExpression

    let makeJsObject pairs =
        pairs |> Seq.map (fun (name, value) ->
            let prop, computed = memberFromName name
            ObjectMember.objectProperty(prop, value, computed_=computed))
        |> Seq.toArray
        |> Expression.objectExpression

    let assign range left right =
        Expression.assignmentExpression(AssignEqual, left, right, ?loc=range)

    /// Immediately Invoked Function Expression
    let iife (com: IBabelCompiler) ctx (expr: Fable.Expr) =
        let _, body = com.TransformFunction(ctx, None, [], expr)
        // Use an arrow function in case we need to capture `this`
        Expression.callExpression(Expression.arrowFunctionExpression([||], body), [||])

    let multiVarDeclaration kind (variables: (Identifier * Expression option) list) =
        let varDeclarators =
            // TODO: Log error if there're duplicated non-empty var declarations
            variables
            |> List.distinctBy (fun (Identifier(name=name), _value) -> name)
            |> List.mapToArray (fun (id, value) ->
                VariableDeclarator(id |> Pattern.Identifier, value))
        Statement.variableDeclaration(kind, varDeclarators)

    let varDeclaration (var: Pattern) (isMutable: bool) value =
        let kind = if isMutable then Let else Const
        VariableDeclaration.variableDeclaration(var, value, kind)

    let restElement (var: Pattern) =
        Pattern.restElement(var)

    let callSuper (args: Expression list) =
        Expression.callExpression(Super(None), List.toArray args)

    let callSuperAsStatement (args: Expression list) =
        ExpressionStatement(callSuper args)

    let makeClassConstructor args body =
        ClassMember.classMethod(ClassImplicitConstructor, Expression.identifier("constructor"), args, body)

    let callFunction r funcExpr (args: Expression list) =
        Expression.callExpression(funcExpr, List.toArray args, ?loc=r)

    let callFunctionWithThisContext r funcExpr (args: Expression list) =
        let args = thisExpr::args |> List.toArray
        Expression.callExpression(get None funcExpr "call", args, ?loc=r)

    let emitExpression range (txt: string) args =
        EmitExpression (txt, List.toArray args, ?loc=range)

    let undefined range =
//        Undefined(?loc=range) :> Expression
        Expression.unaryExpression(UnaryVoid, Expression.numericLiteral(0.), ?loc=range)

    let getGenericTypeParams (types: Fable.Type list) =
        let rec getGenParams = function
            | Fable.GenericParam name -> [name]
            | t -> t.Generics |> List.collect getGenParams
        types
        |> List.collect getGenParams
        |> Set.ofList

    let uncurryLambdaType t =
        let rec uncurryLambdaArgs acc = function
            | Fable.LambdaType(paramType, returnType) ->
                uncurryLambdaArgs (paramType::acc) returnType
            | t -> List.rev acc, t
        uncurryLambdaArgs [] t

    type MemberKind =
        | ClassConstructor
        | NonAttached of funcName: string
        | Attached of isStatic: bool

    let getMemberArgsAndBody (com: IBabelCompiler) ctx kind hasSpread (args: Fable.Ident list) (body: Fable.Expr) =
        let funcName, genTypeParams, args, body =
            match kind, args with
            | Attached(isStatic=false), (thisArg::args) ->
                let genTypeParams = Set.difference (getGenericTypeParams [thisArg.Type]) ctx.ScopedTypeParams
                let body =
                    // TODO: If ident is not captured maybe we can just replace it with "this"
                    if FableTransforms.isIdentUsed thisArg.Name body then
                        let thisKeyword = Fable.IdentExpr { thisArg with Name = "this" }
                        Fable.Let(thisArg, thisKeyword, body)
                    else body
                None, genTypeParams, args, body
            | Attached(isStatic=true), _
            | ClassConstructor, _ -> None, ctx.ScopedTypeParams, args, body
            | NonAttached funcName, _ -> Some funcName, Set.empty, args, body
            | _ -> None, Set.empty, args, body

        let ctx = { ctx with ScopedTypeParams = Set.union ctx.ScopedTypeParams genTypeParams }
        let args, body, returnType, typeParamDecl = transformFunctionWithAnnotations com ctx funcName args body

        let typeParamDecl =
            if com.Options.Language = TypeScript then
                makeTypeParamDecl genTypeParams |> mergeTypeParamDecls typeParamDecl
            else typeParamDecl

        let args =
            let len = Array.length args
            if not hasSpread || len = 0 then args
            else [|
                if len > 1 then
                    yield! args.[..len-2]
                yield restElement args.[len-1]
            |]

        args, body, returnType, typeParamDecl

    let getUnionCaseName (uci: Fable.UnionCase) =
        match uci.CompiledName with Some cname -> cname | None -> uci.Name

    let getUnionExprTag (com: IBabelCompiler) ctx r (fableExpr: Fable.Expr) =
        let expr = com.TransformAsExpr(ctx, fableExpr)
        getExpr r expr (Expression.stringLiteral("tag"))

    /// Wrap int expressions with `| 0` to help optimization of JS VMs
    let wrapIntExpression typ (e: Expression) =
        match e, typ with
        | Literal(NumericLiteral(_)), _ -> e
        // TODO: Unsigned ints seem to cause problems, should we check only Int32 here?
        | _, Fable.Number(Int8 | Int16 | Int32)
        | _, Fable.Enum _ ->
            Expression.binaryExpression(BinaryOrBitwise, e, Expression.numericLiteral(0.))
        | _ -> e

    let wrapExprInBlockWithReturn e =
        BlockStatement([| Statement.returnStatement(e)|])

    let makeArrowFunctionExpression _name (args, (body: BlockStatement), returnType, typeParamDecl): Expression =
        Expression.arrowFunctionExpression(args, body, ?returnType=returnType, ?typeParameters=typeParamDecl)

    let makeFunctionExpression name (args, (body: Expression), returnType, typeParamDecl): Expression =
        let id = name |> Option.map Identifier.identifier
        let body = wrapExprInBlockWithReturn body
        Expression.functionExpression(args, body, ?id=id, ?returnType=returnType, ?typeParameters=typeParamDecl)

    let optimizeTailCall (com: IBabelCompiler) (ctx: Context) range (tc: ITailCallOpportunity) args =
        let rec checkCrossRefs tempVars allArgs = function
            | [] -> tempVars
            | (argId, _arg)::rest ->
                let found = allArgs |> List.exists (FableTransforms.deepExists (function
                    | Fable.IdentExpr i -> argId = i.Name
                    | _ -> false))
                let tempVars =
                    if found then
                        let tempVarName = getUniqueNameInDeclarationScope ctx (argId + "_tmp")
                        Map.add argId tempVarName tempVars
                    else tempVars
                checkCrossRefs tempVars allArgs rest
        ctx.OptimizeTailCall()
        let zippedArgs = List.zip tc.Args args
        let tempVars = checkCrossRefs Map.empty args zippedArgs
        let tempVarReplacements = tempVars |> Map.map (fun _ v -> makeIdentExpr v)
        [|
            // First declare temp variables
            for (KeyValue(argId, tempVar)) in tempVars do
                yield varDeclaration (Pattern.identifier(tempVar)) false (Expression.identifier(argId)) |> Declaration.VariableDeclaration |> Declaration
            // Then assign argument expressions to the original argument identifiers
            // See https://github.com/fable-compiler/Fable/issues/1368#issuecomment-434142713
            for (argId, arg) in zippedArgs do
                let arg = FableTransforms.replaceValues tempVarReplacements arg
                let arg = com.TransformAsExpr(ctx, arg)
                yield assign None (Expression.identifier(argId)) arg |> ExpressionStatement
            yield Statement.continueStatement(Identifier.identifier(tc.Label), ?loc=range)
        |]

    let transformImport (com: IBabelCompiler) ctx r (selector: string) (path: string) =
        let selector, parts =
            let parts = Array.toList(selector.Split('.'))
            parts.Head, parts.Tail
        com.GetImportExpr(ctx, selector, path, r)
        |> getParts parts

    let transformCast (com: IBabelCompiler) (ctx: Context) t tag e: Expression =
        // HACK: Try to optimize some patterns after FableTransforms and fill some gaps in Fable.AST
        let optimized =
            match tag with
            | Some "generic" ->
                match e with
                | Fable.Value(Fable.TypeInfo t, r) -> transformTypeInfo com ctx r None t |> Some
                | _ -> None
            | Some (Naming.StartsWith "optimizable:" optimization) ->
                match optimization, e with
                | "array", Fable.Call(_,info,_,_) ->
                    match info.Args with
                    | [Replacements.ArrayOrListLiteral(vals,_)] ->
                        Fable.Value(Fable.NewArray(vals, Fable.Any), e.Range)
                        |> transformAsExpr com ctx
                        |> Some
                    | _ -> None
                | "pojo", Fable.Call(_,info,_,_) ->
                    let toPojo caseRule keyValueList =
                        Replacements.makePojo com caseRule keyValueList
                        |> Option.map (transformAsExpr com ctx)
                    match info.Args with
                    | keyValueList::caseRule::_ -> toPojo (Some caseRule) keyValueList
                    | keyValueList::_ -> toPojo None keyValueList
                    | _ -> None
                | "function", Fable.Delegate(args, body, name) ->
                    let args, body, returnType, typeParamDecl = transformFunctionWithAnnotations com ctx name args body
                    Expression.functionExpression(args, body, ?returnType=returnType, ?typeParameters=typeParamDecl) |> Some
                | _ -> None
            | _ -> None

        match optimized, t with
        | Some e, _ -> e
        // Optimization for (numeric) array or list literals casted to seq
        // Done at the very end of the compile pipeline to get more opportunities
        // of matching cast and literal expressions after resolving pipes, inlining...
        | None, Fable.DeclaredType(ent,[_]) ->
            match ent.FullName, e with
            | Types.ienumerableGeneric, Replacements.ArrayOrListLiteral(exprs, _) ->
                makeArray com ctx exprs
            | _ -> com.TransformAsExpr(ctx, e)
        | _ -> com.TransformAsExpr(ctx, e)

    let transformCurry (com: IBabelCompiler) (ctx: Context) _r expr arity: Expression =
        com.TransformAsExpr(ctx, Replacements.curryExprAtRuntime com arity expr)

    let transformValue (com: IBabelCompiler) (ctx: Context) r value: Expression =
        match value with
        | Fable.BaseValue(None,_) -> Super(None)
        | Fable.BaseValue(Some boundIdent,_) -> identAsExpr boundIdent
        | Fable.ThisValue _ -> Expression.thisExpression()
        | Fable.TypeInfo t ->
            if com.Options.NoReflection then addErrorAndReturnNull com r "Reflection is disabled"
            else transformTypeInfo com ctx r (Some Map.empty) t
        | Fable.Null _t ->
            // if com.Options.typescript
            //     let ta = typeAnnotation com ctx t |> TypeAnnotation |> Some
            //     upcast Identifier("null", ?typeAnnotation=ta, ?loc=r)
            // else
                Expression.nullLiteral(?loc=r)
        | Fable.UnitConstant -> undefined r
        | Fable.BoolConstant x -> Expression.booleanLiteral(x, ?loc=r)
        | Fable.CharConstant x -> Expression.stringLiteral(string x, ?loc=r)
        | Fable.StringConstant x -> Expression.stringLiteral(x, ?loc=r)
        | Fable.NumberConstant (x,_) -> Expression.numericLiteral(x, ?loc=r)
        | Fable.RegexConstant (source, flags) -> Expression.regExpLiteral(source, flags, ?loc=r)
        | Fable.NewArray (values, typ) -> makeTypedArray com ctx typ values
        | Fable.NewArrayFrom (size, typ) -> makeTypedAllocatedFrom com ctx typ size
        | Fable.NewTuple vals -> makeArray com ctx vals
        // | Fable.NewList (headAndTail, _) when List.contains "FABLE_LIBRARY" com.Options.Define ->
        //     makeList com ctx r headAndTail
        // Optimization for bundle size: compile list literals as List.ofArray
        | Fable.NewList (headAndTail, _) ->
            let rec getItems acc = function
                | None -> List.rev acc, None
                | Some(head, Fable.Value(Fable.NewList(tail, _),_)) -> getItems (head::acc) tail
                | Some(head, tail) -> List.rev (head::acc), Some tail
            match getItems [] headAndTail with
            | [], None ->
                libCall com ctx r "List" "empty" [||]
            | [TransformExpr com ctx expr], None ->
                libCall com ctx r "List" "singleton" [|expr|]
            | exprs, None ->
                [|makeArray com ctx exprs|]
                |> libCall com ctx r "List" "ofArray"
            | [TransformExpr com ctx head], Some(TransformExpr com ctx tail) ->
                libCall com ctx r "List" "cons" [|head; tail|]
            | exprs, Some(TransformExpr com ctx tail) ->
                [|makeArray com ctx exprs; tail|]
                |> libCall com ctx r "List" "ofArrayWithTail"
        | Fable.NewOption (value, t) ->
            match value with
            | Some (TransformExpr com ctx e) ->
                if mustWrapOption t
                then libCall com ctx r "Option" "some" [|e|]
                else e
            | None -> undefined r
        | Fable.EnumConstant(x,_) ->
            com.TransformAsExpr(ctx, x)
        | Fable.NewRecord(values, ent, genArgs) ->
            let ent = com.GetEntity(ent)
            let values = List.mapToArray (fun x -> com.TransformAsExpr(ctx, x)) values
            let consRef = ent |> jsConstructor com ctx
            let typeParamInst =
                if com.Options.Language = TypeScript && (ent.FullName = Types.reference)
                then makeGenTypeParamInst com ctx genArgs
                else None
            Expression.newExpression(consRef, values, ?typeArguments=typeParamInst, ?loc=r)
        | Fable.NewAnonymousRecord(values, fieldNames, _genArgs) ->
            let values = List.mapToArray (fun x -> com.TransformAsExpr(ctx, x)) values
            Array.zip fieldNames values |> makeJsObject
        | Fable.NewUnion(values, tag, ent, genArgs) ->
            let ent = com.GetEntity(ent)
            let values = List.map (fun x -> com.TransformAsExpr(ctx, x)) values
            let consRef = ent |> jsConstructor com ctx
            let typeParamInst =
                if com.Options.Language = TypeScript
                then makeGenTypeParamInst com ctx genArgs
                else None
            // let caseName = ent.UnionCases |> List.item tag |> getUnionCaseName |> ofString
            let values = (ofInt tag)::values |> List.toArray
            Expression.newExpression(consRef, values, ?typeArguments=typeParamInst, ?loc=r)

    let enumerator2iterator com ctx =
        let enumerator = Expression.callExpression(get None (Expression.identifier("this")) "GetEnumerator", [||])
        BlockStatement([| Statement.returnStatement(libCall com ctx None "Util" "toIterator" [|enumerator|])|])

    let extractBaseExprFromBaseCall (com: IBabelCompiler) (ctx: Context) (baseType: Fable.DeclaredType option) baseCall =
        match baseCall, baseType with
        | Some (Fable.Call(baseRef, info, _, _)), _ ->
            let baseExpr =
                match baseRef with
                | Fable.IdentExpr id -> typedIdent com ctx id |> Expression.Identifier
                | _ -> transformAsExpr com ctx baseRef
            let args = transformCallArgs com ctx None (CallInfo info)
            Some (baseExpr, args)
        | Some (Fable.Value _), Some baseType ->
            // let baseEnt = com.GetEntity(baseType.Entity)
            // let entityName = FSharp2Fable.Helpers.getEntityDeclarationName com baseType.Entity
            // let entityType = FSharp2Fable.Util.getEntityType baseEnt
            // let baseRefId = makeTypedIdent entityType entityName
            // let baseExpr = (baseRefId |> typedIdent com ctx) :> Expression
            // Some (baseExpr, []) // default base constructor
            let range = baseCall |> Option.bind (fun x -> x.Range)
            $"Ignoring base call for %s{baseType.Entity.FullName}" |> addWarning com [] range
            None
        | Some _, _ ->
            let range = baseCall |> Option.bind (fun x -> x.Range)
            "Unexpected base call expression, please report" |> addError com [] range
            None
        | None, _ ->
            None

    let transformObjectExpr (com: IBabelCompiler) ctx (members: Fable.MemberDecl list) baseCall: Expression =
        let compileAsClass =
            Option.isSome baseCall || members |> List.exists (fun m ->
                // Optimization: Object literals with getters and setters are very slow in V8
                // so use a class expression instead. See https://github.com/fable-compiler/Fable/pull/2165#issuecomment-695835444
                m.Info.IsSetter || (m.Info.IsGetter && canHaveSideEffects m.Body))

        let makeMethod kind prop computed hasSpread args body =
            let args, body, returnType, typeParamDecl =
                getMemberArgsAndBody com ctx (Attached(isStatic=false)) hasSpread args body
            ObjectMember.objectMethod(kind, prop, args, body, computed_=computed,
                ?returnType=returnType, ?typeParameters=typeParamDecl)

        let members =
            members |> List.collect (fun memb ->
                let info = memb.Info
                let prop, computed = memberFromName memb.Name
                // If compileAsClass is false, it means getters don't have side effects
                // and can be compiled as object fields (see condition above)
                if info.IsValue || (not compileAsClass && info.IsGetter) then
                    [ObjectMember.objectProperty(prop, com.TransformAsExpr(ctx, memb.Body), computed_=computed)]
                elif info.IsGetter then
                    [makeMethod ObjectGetter prop computed false memb.Args memb.Body]
                elif info.IsSetter then
                    [makeMethod ObjectSetter prop computed false memb.Args memb.Body]
                elif info.IsEnumerator then
                    let method = makeMethod ObjectMeth prop computed info.HasSpread memb.Args memb.Body
                    let iterator =
                        let prop, computed = memberFromName "Symbol.iterator"
                        let body = enumerator2iterator com ctx
                        ObjectMember.objectMethod(ObjectMeth, prop, [||], body, computed_=computed)
                    [method; iterator]
                else
                    [makeMethod ObjectMeth prop computed info.HasSpread memb.Args memb.Body]
            )

        if not compileAsClass then
            Expression.objectExpression(List.toArray  members)
        else
            let classMembers =
                members |> List.choose (function
                    | ObjectProperty(key, value, computed) ->
                        ClassMember.classProperty(key, value, computed_=computed) |> Some
                    | ObjectMethod(kind, key, parameters, body, computed, returnType, typeParameters, _) ->
                        let kind =
                            match kind with
                            | "get" -> ClassGetter
                            | "set" -> ClassSetter
                            | _ -> ClassFunction
                        ClassMember.classMethod(kind, key, parameters, body, computed_=computed,
                            ?returnType=returnType, ?typeParameters=typeParameters) |> Some)

            let baseExpr, classMembers =
                baseCall
                |> extractBaseExprFromBaseCall com ctx None
                |> Option.map (fun (baseExpr, baseArgs) ->
                    let consBody = BlockStatement([|callSuperAsStatement baseArgs|])
                    let cons = makeClassConstructor [||]  consBody
                    Some baseExpr, cons::classMembers
                )
                |> Option.defaultValue (None, classMembers)

            let classBody = ClassBody.classBody(List.toArray classMembers)
            let classExpr = Expression.classExpression(classBody, ?superClass=baseExpr)
            Expression.newExpression(classExpr, [||])

    let transformCallArgs (com: IBabelCompiler) ctx (r: SourceLocation option) (info: ArgsInfo) =
        let tryGetParamObjInfo (memberInfo: Fable.CallMemberInfo) =
            let tryGetParamNames (parameters: Fable.Parameter list) =
                (Some [], parameters) ||> List.fold (fun acc p ->
                    match acc, p.Name with
                    | Some acc, Some name -> Some(name::acc)
                    | _ -> None)
                |> function
                    | Some names -> List.rev names |> Some
                    | None ->
                        "ParamObj cannot be used with unnamed parameters"
                        |> addWarning com [] r
                        None

            match memberInfo.CurriedParameterGroups, memberInfo.DeclaringEntity with
            // Check only members with multiple non-curried arguments
            | [parameters], Some ent when not (List.isEmpty parameters) ->
                // Skip check for core assemblies
                match ent.Path with
                | Fable.CoreAssemblyName _ -> None
                | _ -> com.TryGetEntity(ent)
                |> Option.bind (fun ent ->
                    if ent.IsFSharpModule then None
                    else
                        ent.MembersFunctionsAndValues
                        |> Seq.tryFind (fun m ->
                            m.IsInstance = memberInfo.IsInstance
                            && m.CompiledName = memberInfo.CompiledName
                            && match m.CurriedParameterGroups with
                                | [parameters2] when List.sameLength parameters parameters2 ->
                                    List.zip parameters parameters2 |> List.forall (fun (p1, p2) -> typeEquals true p1.Type p2.Type)
                                | _ -> false))
                |> Option.bind (fun m ->
                    m.Attributes |> Seq.tryPick (fun a ->
                        if a.Entity.FullName = Atts.paramObject then
                            let index =
                                match a.ConstructorArgs with
                                | (:? int as index)::_ -> index
                                | _ -> 0
                            tryGetParamNames parameters |> Option.map (fun paramNames ->
                                {| Index = index; Parameters = paramNames |})
                        else None
                    ))
                | _ -> None

        let paramObjInfo, hasSpread, args =
            match info with
            | CallInfo i ->
                let paramObjInfo =
                    match i.CallMemberInfo with
                    // ParamObject is not compatible with arg spread
                    | Some mi when not i.HasSpread -> tryGetParamObjInfo mi
                    | _ -> None
                paramObjInfo, i.HasSpread, i.Args
            | NoCallInfo args -> None, false, args

        let args, objArg =
            match paramObjInfo with
            | None -> args, None
            | Some i when i.Index > List.length args -> args, None
            | Some i ->
                let args, objValues = List.splitAt i.Index args
                let _, objKeys = List.splitAt i.Index i.Parameters
                let objKeys = List.take (List.length objValues) objKeys
                let objArg =
                    List.zip objKeys objValues
                    |> List.choose (function
                        | k, Fable.Value(Fable.NewOption(value,_),_) -> value |> Option.map (fun v -> k, v)
                        | k, v -> Some(k, v))
                    |> List.map (fun (k, v) -> k, com.TransformAsExpr(ctx, v))
                    |> makeJsObject
                args, Some objArg

        let args =
            match args with
            | []
            | [MaybeCasted(Fable.Value(Fable.UnitConstant,_))] -> []
            | args when hasSpread ->
                match List.rev args with
                | [] -> []
                | (Replacements.ArrayOrListLiteral(spreadArgs,_))::rest ->
                    let rest = List.rev rest |> List.map (fun e -> com.TransformAsExpr(ctx, e))
                    rest @ (List.map (fun e -> com.TransformAsExpr(ctx, e)) spreadArgs)
                | last::rest ->
                    let rest = List.rev rest |> List.map (fun e -> com.TransformAsExpr(ctx, e))
                    rest @ [Expression.spreadElement(com.TransformAsExpr(ctx, last))]
            | args -> List.map (fun e -> com.TransformAsExpr(ctx, e)) args

        match objArg with
        | None -> args
        | Some objArg -> args @ [objArg]

    let resolveExpr t strategy babelExpr: Statement =
        match strategy with
        | None | Some ReturnUnit -> ExpressionStatement(babelExpr)
        // TODO: Where to put these int wrappings? Add them also for function arguments?
        | Some Return ->  Statement.returnStatement(wrapIntExpression t babelExpr)
        | Some(Assign left) -> ExpressionStatement(assign None left babelExpr)
        | Some(Target left) -> ExpressionStatement(assign None (left |> Expression.Identifier) babelExpr)

    let transformOperation com ctx range opKind: Expression =
        match opKind with
        | Fable.Unary(op, TransformExpr com ctx expr) ->
            Expression.unaryExpression(op, expr, ?loc=range)

        | Fable.Binary(op, TransformExpr com ctx left, TransformExpr com ctx right) ->
            Expression.binaryExpression(op, left, right, ?loc=range)

        | Fable.Logical(op, TransformExpr com ctx left, TransformExpr com ctx right) ->
            Expression.logicalExpression(left, op, right, ?loc=range)

    let transformEmit (com: IBabelCompiler) ctx range (info: Fable.EmitInfo) =
        let macro = info.Macro
        let info = info.CallInfo
        let thisArg = info.ThisArg |> Option.map (fun e -> com.TransformAsExpr(ctx, e)) |> Option.toList
        transformCallArgs com ctx range (CallInfo info)
        |> List.append thisArg
        |> emitExpression range macro

    let transformCall (com: IBabelCompiler) ctx range callee (callInfo: Fable.CallInfo) =
        let callee = com.TransformAsExpr(ctx, callee)
        let args = transformCallArgs com ctx range (CallInfo callInfo)
        match callInfo.ThisArg with
        | Some(TransformExpr com ctx thisArg) -> callFunction range callee (thisArg::args)
        | None when callInfo.IsJsConstructor -> Expression.newExpression(callee, List.toArray args, ?loc=range)
        | None -> callFunction range callee args

    let transformCurriedApply com ctx range (TransformExpr com ctx applied) args =
        match transformCallArgs com ctx range (NoCallInfo args) with
        | [] -> callFunction range applied []
        | args -> (applied, args) ||> List.fold (fun e arg -> callFunction range e [arg])

    let transformCallAsStatements com ctx range t returnStrategy callee callInfo =
        let argsLen (i: Fable.CallInfo) =
            List.length i.Args + (if Option.isSome i.ThisArg then 1 else 0)
        // Warn when there's a recursive call that couldn't be optimized?
        match returnStrategy, ctx.TailCallOpportunity with
        | Some(Return|ReturnUnit), Some tc when tc.IsRecursiveRef(callee)
                                            && argsLen callInfo = List.length tc.Args ->
            let args =
                match callInfo.ThisArg with
                | Some thisArg -> thisArg::callInfo.Args
                | None -> callInfo.Args
            optimizeTailCall com ctx range tc args
        | _ ->
            [|transformCall com ctx range callee callInfo |> resolveExpr t returnStrategy|]

    let transformCurriedApplyAsStatements com ctx range t returnStrategy callee args =
        // Warn when there's a recursive call that couldn't be optimized?
        match returnStrategy, ctx.TailCallOpportunity with
        | Some(Return|ReturnUnit), Some tc when tc.IsRecursiveRef(callee)
                                            && List.sameLength args tc.Args ->
            optimizeTailCall com ctx range tc args
        | _ ->
            [|transformCurriedApply com ctx range callee args |> resolveExpr t returnStrategy|]

    // When expecting a block, it's usually not necessary to wrap it
    // in a lambda to isolate its variable context
    let transformBlock (com: IBabelCompiler) ctx ret expr: BlockStatement =
        com.TransformAsStatements(ctx, ret, expr) |> BlockStatement

    let transformTryCatch com ctx r returnStrategy (body, catch, finalizer) =
        // try .. catch statements cannot be tail call optimized
        let ctx = { ctx with TailCallOpportunity = None }
        let handler =
            catch |> Option.map (fun (param, body) ->
                CatchClause.catchClause(identAsPattern param, transformBlock com ctx returnStrategy body))
        let finalizer =
            finalizer |> Option.map (transformBlock com ctx None)
        [|Statement.tryStatement(transformBlock com ctx returnStrategy body,
            ?handler=handler, ?finalizer=finalizer, ?loc=r)|]

    let rec transformIfStatement (com: IBabelCompiler) ctx r ret guardExpr thenStmnt elseStmnt =
        match com.TransformAsExpr(ctx, guardExpr) with
        | Literal(BooleanLiteral(value=value)) when value ->
            com.TransformAsStatements(ctx, ret, thenStmnt)
        | Literal(BooleanLiteral(value=value)) when not value ->
            com.TransformAsStatements(ctx, ret, elseStmnt)
        | guardExpr ->
            let thenStmnt = transformBlock com ctx ret thenStmnt
            match com.TransformAsStatements(ctx, ret, elseStmnt) with
            | [||] -> Statement.ifStatement(guardExpr, thenStmnt, ?loc=r)
            | [|elseStmnt|] -> Statement.ifStatement(guardExpr, thenStmnt, elseStmnt, ?loc=r)
            | statements -> Statement.ifStatement(guardExpr, thenStmnt, Statement.blockStatement(statements), ?loc=r)
            |> Array.singleton

    let transformGet (com: IBabelCompiler) ctx range typ fableExpr kind =
        match kind with
        | Fable.ByKey key ->
            let fableExpr =
                match fableExpr with
                // If we're accessing a virtual member with default implementation (see #701)
                // from base class, we can use `super` in JS so we don't need the bound this arg
                | Fable.Value(Fable.BaseValue(_,t), r) -> Fable.Value(Fable.BaseValue(None, t), r)
                | _ -> fableExpr
            let expr = com.TransformAsExpr(ctx, fableExpr)
            match key with
            | Fable.ExprKey(TransformExpr com ctx prop) -> getExpr range expr prop
            | Fable.FieldKey field -> get range expr field.Name

        | Fable.ListHead ->
            // get range (com.TransformAsExpr(ctx, fableExpr)) "head"
            libCall com ctx range "List" "head" [|com.TransformAsExpr(ctx, fableExpr)|]

        | Fable.ListTail ->
            // get range (com.TransformAsExpr(ctx, fableExpr)) "tail"
            libCall com ctx range "List" "tail" [|com.TransformAsExpr(ctx, fableExpr)|]

        | Fable.TupleIndex index ->
            match fableExpr with
            // TODO: Check the erased expressions don't have side effects?
            | Fable.Value(Fable.NewTuple exprs, _) ->
                com.TransformAsExpr(ctx, List.item index exprs)
            | TransformExpr com ctx expr -> getExpr range expr (ofInt index)

        | Fable.OptionValue ->
            let expr = com.TransformAsExpr(ctx, fableExpr)
            if mustWrapOption typ || com.Options.Language = TypeScript
            then libCall com ctx None "Option" "value" [|expr|]
            else expr

        | Fable.UnionTag ->
            getUnionExprTag com ctx range fableExpr

        | Fable.UnionField(index, _) ->
            let expr = com.TransformAsExpr(ctx, fableExpr)
            getExpr range (getExpr None expr (Expression.stringLiteral("fields"))) (ofInt index)

    let transformSet (com: IBabelCompiler) ctx range fableExpr (value: Fable.Expr) kind =
        let expr = com.TransformAsExpr(ctx, fableExpr)
        let value = com.TransformAsExpr(ctx, value) |> wrapIntExpression value.Type
        let ret =
            match kind with
            | None -> expr
            | Some(Fable.FieldKey fi) -> get None expr fi.Name
            | Some(Fable.ExprKey(TransformExpr com ctx e)) -> getExpr None expr e
        assign range ret value

    let transformBindingExprBody (com: IBabelCompiler) (ctx: Context) (var: Fable.Ident) (value: Fable.Expr) =
        match value with
        | Function(args, body) ->
            let name = Some var.Name
            transformFunctionWithAnnotations com ctx name args body
            |> makeArrowFunctionExpression name
        | _ ->
            if var.IsMutable then
                com.TransformAsExpr(ctx, value)
            else
                com.TransformAsExpr(ctx, value) |> wrapIntExpression value.Type

    let transformBindingAsExpr (com: IBabelCompiler) ctx (var: Fable.Ident) (value: Fable.Expr) =
        transformBindingExprBody com ctx var value
        |> assign None (identAsExpr var)

    let transformBindingAsStatements (com: IBabelCompiler) ctx (var: Fable.Ident) (value: Fable.Expr) =
        if isJsStatement ctx false value then
            let varPattern, varExpr = identAsPattern var, identAsExpr var
            let decl = Statement.variableDeclaration(varPattern)
            let body = com.TransformAsStatements(ctx, Some(Assign varExpr), value)
            Array.append [|decl|] body
        else
            let value = transformBindingExprBody com ctx var value
            let decl = varDeclaration (identAsPattern var) var.IsMutable value |> Declaration.VariableDeclaration |> Declaration
            [|decl|]

    let transformTest (com: IBabelCompiler) ctx range kind expr: Expression =
        match kind with
        | Fable.TypeTest t ->
            transformTypeTest com ctx range expr t
        | Fable.OptionTest nonEmpty ->
            let op = if nonEmpty then BinaryUnequal else BinaryEqual
            Expression.binaryExpression(op, com.TransformAsExpr(ctx, expr), Expression.nullLiteral(), ?loc=range)
        | Fable.ListTest nonEmpty ->
            let expr = com.TransformAsExpr(ctx, expr)
            // let op = if nonEmpty then BinaryUnequal else BinaryEqual
            // Expression.binaryExpression(op, get None expr "tail", Expression.nullLiteral(), ?loc=range)
            let expr = libCall com ctx range "List" "isEmpty" [|expr|]
            if nonEmpty then Expression.unaryExpression(UnaryNot, expr, ?loc=range) else expr
        | Fable.UnionCaseTest tag ->
            let expected = ofInt tag
            let actual = getUnionExprTag com ctx None expr
            Expression.binaryExpression(BinaryEqualStrict, actual, expected, ?loc=range)

    let transformSwitch (com: IBabelCompiler) ctx useBlocks returnStrategy evalExpr cases defaultCase: Statement =
        let consequent caseBody =
            if useBlocks then [|Statement.blockStatement(caseBody)|] else caseBody
        let cases =
            cases |> List.collect (fun (guards, expr) ->
                // Remove empty branches
                match returnStrategy, expr, guards with
                | None, Fable.Value(Fable.UnitConstant,_), _
                | _, _, [] -> []
                | _, _, guards ->
                    let guards, lastGuard = List.splitLast guards
                    let guards = guards |> List.map (fun e -> SwitchCase.switchCase([||], com.TransformAsExpr(ctx, e)))
                    let caseBody = com.TransformAsStatements(ctx, returnStrategy, expr)
                    let caseBody =
                        match returnStrategy with
                        | Some Return -> caseBody
                        | _ -> Array.append caseBody [|Statement.breakStatement()|]
                    guards @ [SwitchCase.switchCase(consequent caseBody, com.TransformAsExpr(ctx, lastGuard))]
                )
        let cases =
            match defaultCase with
            | Some expr ->
                let defaultCaseBody = com.TransformAsStatements(ctx, returnStrategy, expr)
                cases @ [SwitchCase.switchCase(consequent defaultCaseBody)]
            | None -> cases
        Statement.switchStatement(com.TransformAsExpr(ctx, evalExpr), List.toArray cases)

    let matchTargetIdentAndValues idents values =
        if List.isEmpty idents then []
        elif List.sameLength idents values then List.zip idents values
        else failwith "Target idents/values lengths differ"

    let getDecisionTargetAndBindValues (com: IBabelCompiler) (ctx: Context) targetIndex boundValues =
        let idents, target = getDecisionTarget ctx targetIndex
        let identsAndValues = matchTargetIdentAndValues idents boundValues
        if not com.Options.DebugMode then
            let bindings, replacements =
                (([], Map.empty), identsAndValues)
                ||> List.fold (fun (bindings, replacements) (ident, expr) ->
                    if canHaveSideEffects expr then
                        (ident, expr)::bindings, replacements
                    else
                        bindings, Map.add ident.Name expr replacements)
            let target = FableTransforms.replaceValues replacements target
            List.rev bindings, target
        else
            identsAndValues, target

    let transformDecisionTreeSuccessAsExpr (com: IBabelCompiler) (ctx: Context) targetIndex boundValues =
        let bindings, target = getDecisionTargetAndBindValues com ctx targetIndex boundValues
        match bindings with
        | [] -> com.TransformAsExpr(ctx, target)
        | bindings ->
            let target = List.rev bindings |> List.fold (fun e (i,v) -> Fable.Let(i,v,e)) target
            com.TransformAsExpr(ctx, target)

    let transformDecisionTreeSuccessAsStatements (com: IBabelCompiler) (ctx: Context) returnStrategy targetIndex boundValues: Statement[] =
        match returnStrategy with
        | Some(Target targetId) ->
            let idents, _ = getDecisionTarget ctx targetIndex
            let assignments =
                matchTargetIdentAndValues idents boundValues
                |> List.mapToArray (fun (id, TransformExpr com ctx value) ->
                    assign None (identAsExpr id) value |> ExpressionStatement)
            let targetAssignment = assign None (targetId |> Expression.Identifier) (ofInt targetIndex) |> ExpressionStatement
            Array.append [|targetAssignment|] assignments
        | ret ->
            let bindings, target = getDecisionTargetAndBindValues com ctx targetIndex boundValues
            let bindings = bindings |> Seq.collect (fun (i, v) -> transformBindingAsStatements com ctx i v) |> Seq.toArray
            Array.append bindings (com.TransformAsStatements(ctx, ret, target))

    let transformDecisionTreeAsSwitch expr =
        let (|Equals|_|) = function
            | Fable.Operation(Fable.Binary(BinaryEqualStrict, expr, right), _, _) ->
                Some(expr, right)
            | Fable.Test(expr, Fable.UnionCaseTest tag, _) ->
                let evalExpr = Fable.Get(expr, Fable.UnionTag, Fable.Number Int32, None)
                let right = Fable.NumberConstant(float tag, Int32) |> makeValue None
                Some(evalExpr, right)
            | _ -> None
        let sameEvalExprs evalExpr1 evalExpr2 =
            match evalExpr1, evalExpr2 with
            | Fable.IdentExpr i1, Fable.IdentExpr i2
            | Fable.Get(Fable.IdentExpr i1,Fable.UnionTag,_,_), Fable.Get(Fable.IdentExpr i2,Fable.UnionTag,_,_) ->
                i1.Name = i2.Name
            | Fable.Get(Fable.IdentExpr i1,Fable.ByKey(Fable.FieldKey k1),_,_), Fable.Get(Fable.IdentExpr i2,Fable.ByKey(Fable.FieldKey k2),_,_) ->
                i1.Name = i2.Name && k1.Name = k2.Name
            | _ -> false
        let rec checkInner cases evalExpr = function
            | Fable.IfThenElse(Equals(evalExpr2, caseExpr),
                               Fable.DecisionTreeSuccess(targetIndex, boundValues, _), treeExpr, _)
                                    when sameEvalExprs evalExpr evalExpr2 ->
                match treeExpr with
                | Fable.DecisionTreeSuccess(defaultTargetIndex, defaultBoundValues, _) ->
                    let cases = (caseExpr, targetIndex, boundValues)::cases |> List.rev
                    Some(evalExpr, cases, (defaultTargetIndex, defaultBoundValues))
                | treeExpr -> checkInner ((caseExpr, targetIndex, boundValues)::cases) evalExpr treeExpr
            | _ -> None
        match expr with
        | Fable.IfThenElse(Equals(evalExpr, caseExpr),
                           Fable.DecisionTreeSuccess(targetIndex, boundValues, _), treeExpr, _) ->
            match checkInner [caseExpr, targetIndex, boundValues] evalExpr treeExpr with
            | Some(evalExpr, cases, defaultCase) ->
                Some(evalExpr, cases, defaultCase)
            | None -> None
        | _ -> None

    let transformDecisionTreeAsExpr (com: IBabelCompiler) (ctx: Context) targets expr: Expression =
        // TODO: Check if some targets are referenced multiple times
        let ctx = { ctx with DecisionTargets = targets }
        com.TransformAsExpr(ctx, expr)

    let groupSwitchCases t (cases: (Fable.Expr * int * Fable.Expr list) list) (defaultIndex, defaultBoundValues) =
        cases
        |> List.groupBy (fun (_,idx,boundValues) ->
            // Try to group cases with some target index and empty bound values
            // If bound values are non-empty use also a non-empty Guid to prevent grouping
            if List.isEmpty boundValues
            then idx, System.Guid.Empty
            else idx, System.Guid.NewGuid())
        |> List.map (fun ((idx,_), cases) ->
            let caseExprs = cases |> List.map Tuple3.item1
            // If there are multiple cases, it means boundValues are empty
            // (see `groupBy` above), so it doesn't mind which one we take as reference
            let boundValues = cases |> List.head |> Tuple3.item3
            caseExprs, Fable.DecisionTreeSuccess(idx, boundValues, t))
        |> function
            | [] -> []
            // Check if the last case can also be grouped with the default branch, see #2357
            | cases when List.isEmpty defaultBoundValues ->
                match List.splitLast cases with
                | cases, (_, Fable.DecisionTreeSuccess(idx, [], _))
                    when idx = defaultIndex -> cases
                | _ -> cases
            | cases -> cases

    let getTargetsWithMultipleReferences expr =
        let rec findSuccess (targetRefs: Map<int,int>) = function
            | [] -> targetRefs
            | expr::exprs ->
                match expr with
                // We shouldn't actually see this, but shortcircuit just in case
                | Fable.DecisionTree _ ->
                    findSuccess targetRefs exprs
                | Fable.DecisionTreeSuccess(idx,_,_) ->
                    let count =
                        Map.tryFind idx targetRefs
                        |> Option.defaultValue 0
                    let targetRefs = Map.add idx (count + 1) targetRefs
                    findSuccess targetRefs exprs
                | expr ->
                    let exprs2 = FableTransforms.getSubExpressions expr
                    findSuccess targetRefs (exprs @ exprs2)
        findSuccess Map.empty [expr] |> Seq.choose (fun kv ->
            if kv.Value > 1 then Some kv.Key else None) |> Seq.toList

    /// When several branches share target create first a switch to get the target index and bind value
    /// and another to execute the actual target
    let transformDecisionTreeWithTwoSwitches (com: IBabelCompiler) ctx returnStrategy
                    (targets: (Fable.Ident list * Fable.Expr) list) treeExpr =
        // Declare target and bound idents
        let targetId = getUniqueNameInDeclarationScope ctx "pattern_matching_result" |> makeIdent
        let multiVarDecl =
            let boundIdents = targets |> List.collect (fun (idents,_) ->
                idents |> List.map (fun id -> typedIdent com ctx id, None))
            multiVarDeclaration Let ((typedIdent com ctx targetId, None)::boundIdents)
        // Transform targets as switch
        let switch2 =
            // TODO: Declare the last case as the default case?
            let cases = targets |> List.mapi (fun i (_,target) -> [makeIntConst i], target)
            transformSwitch com ctx true returnStrategy (targetId |> Fable.IdentExpr) cases None
        // Transform decision tree
        let targetAssign = Target(ident targetId)
        let ctx = { ctx with DecisionTargets = targets }
        match transformDecisionTreeAsSwitch treeExpr with
        | Some(evalExpr, cases, (defaultIndex, defaultBoundValues)) ->
            let cases = groupSwitchCases (Fable.Number Int32) cases (defaultIndex, defaultBoundValues)
            let defaultCase = Fable.DecisionTreeSuccess(defaultIndex, defaultBoundValues, Fable.Number Int32)
            let switch1 = transformSwitch com ctx false (Some targetAssign) evalExpr cases (Some defaultCase)
            [|multiVarDecl; switch1; switch2|]
        | None ->
            let decisionTree = com.TransformAsStatements(ctx, Some targetAssign, treeExpr)
            [| yield multiVarDecl; yield! decisionTree; yield switch2 |]

    let transformDecisionTreeAsStatements (com: IBabelCompiler) (ctx: Context) returnStrategy
                        (targets: (Fable.Ident list * Fable.Expr) list) (treeExpr: Fable.Expr): Statement[] =
        // If some targets are referenced multiple times, hoist bound idents,
        // resolve the decision index and compile the targets as a switch
        let targetsWithMultiRefs =
            if com.Options.Language = TypeScript then [] // no hoisting when compiled with types
            else getTargetsWithMultipleReferences treeExpr
        match targetsWithMultiRefs with
        | [] ->
            let ctx = { ctx with DecisionTargets = targets }
            match transformDecisionTreeAsSwitch treeExpr with
            | Some(evalExpr, cases, (defaultIndex, defaultBoundValues)) ->
                let t = treeExpr.Type
                let cases = cases |> List.map (fun (caseExpr, targetIndex, boundValues) ->
                    [caseExpr], Fable.DecisionTreeSuccess(targetIndex, boundValues, t))
                let defaultCase = Fable.DecisionTreeSuccess(defaultIndex, defaultBoundValues, t)
                [|transformSwitch com ctx true returnStrategy evalExpr cases (Some defaultCase)|]
            | None ->
                com.TransformAsStatements(ctx, returnStrategy, treeExpr)
        | targetsWithMultiRefs ->
            // If the bound idents are not referenced in the target, remove them
            let targets =
                targets |> List.map (fun (idents, expr) ->
                    idents
                    |> List.exists (fun i -> FableTransforms.isIdentUsed i.Name expr)
                    |> function
                        | true -> idents, expr
                        | false -> [], expr)
            let hasAnyTargetWithMultiRefsBoundValues =
                targetsWithMultiRefs |> List.exists (fun idx ->
                    targets.[idx] |> fst |> List.isEmpty |> not)
            if not hasAnyTargetWithMultiRefsBoundValues then
                match transformDecisionTreeAsSwitch treeExpr with
                | Some(evalExpr, cases, (defaultIndex, defaultBoundValues)) ->
                    let t = treeExpr.Type
                    let cases = groupSwitchCases t cases (defaultIndex, defaultBoundValues)
                    let ctx = { ctx with DecisionTargets = targets }
                    let defaultCase = Fable.DecisionTreeSuccess(defaultIndex, defaultBoundValues, t)
                    [|transformSwitch com ctx true returnStrategy evalExpr cases (Some defaultCase)|]
                | None ->
                    transformDecisionTreeWithTwoSwitches com ctx returnStrategy targets treeExpr
            else
                transformDecisionTreeWithTwoSwitches com ctx returnStrategy targets treeExpr

    let rec transformAsExpr (com: IBabelCompiler) ctx (expr: Fable.Expr): Expression =
        match expr with
        | Fable.Unresolved e -> addErrorAndReturnNull com e.Range "Unexpected unresolved expression"

        | Fable.TypeCast(e,t,tag) -> transformCast com ctx t tag e

        | Fable.Curry(e, arity, _, r) -> transformCurry com ctx r e arity

        | Fable.Value(kind, r) -> transformValue com ctx r kind

        | Fable.IdentExpr id -> identAsExpr id

        | Fable.Import({ Selector = selector; Path = path }, _, r) ->
            transformImport com ctx r selector path

        | Fable.Test(expr, kind, range) ->
            transformTest com ctx range kind expr

        | Fable.Lambda(arg, body, name) ->
            transformFunctionWithAnnotations com ctx name [arg] body
            |> makeArrowFunctionExpression name

        | Fable.Delegate(args, body, name) ->
            transformFunctionWithAnnotations com ctx name args body
            |> makeArrowFunctionExpression name

        | Fable.ObjectExpr (members, _, baseCall) ->
           transformObjectExpr com ctx members baseCall

        | Fable.Call(callee, info, _, range) ->
            transformCall com ctx range callee info

        | Fable.CurriedApply(callee, args, _, range) ->
            transformCurriedApply com ctx range callee args

        | Fable.Operation(kind, _, range) ->
            transformOperation com ctx range kind

        | Fable.Get(expr, kind, typ, range) ->
            transformGet com ctx range typ expr kind

        | Fable.IfThenElse(TransformExpr com ctx guardExpr,
                           TransformExpr com ctx thenExpr,
                           TransformExpr com ctx elseExpr, r) ->
            Expression.conditionalExpression(guardExpr, thenExpr, elseExpr, ?loc=r)

        | Fable.DecisionTree(expr, targets) ->
            transformDecisionTreeAsExpr com ctx targets expr

        | Fable.DecisionTreeSuccess(idx, boundValues, _) ->
            transformDecisionTreeSuccessAsExpr com ctx idx boundValues

        | Fable.Set(expr, kind, value, range) ->
            transformSet com ctx range expr value kind

        | Fable.Let(ident, value, body) ->
            if ctx.HoistVars [ident] then
                let assignment = transformBindingAsExpr com ctx ident value
                Expression.sequenceExpression([|assignment; com.TransformAsExpr(ctx, body)|])
            else iife com ctx expr

        | Fable.LetRec(bindings, body) ->
            if ctx.HoistVars(List.map fst bindings) then
                let values = bindings |> List.mapToArray (fun (id, value) ->
                    transformBindingAsExpr com ctx id value)
                Expression.sequenceExpression(Array.append values [|com.TransformAsExpr(ctx, body)|])
            else iife com ctx expr

        | Fable.Sequential exprs ->
            List.mapToArray (fun e -> com.TransformAsExpr(ctx, e)) exprs
            |> Expression.sequenceExpression

        | Fable.Emit(info, _, range) ->
            if info.IsJsStatement then iife com ctx expr
            else transformEmit com ctx range info

        // These cannot appear in expression position in JS, must be wrapped in a lambda
        | Fable.WhileLoop _ | Fable.ForLoop _ | Fable.TryCatch _ ->
            iife com ctx expr

    let rec transformAsStatements (com: IBabelCompiler) ctx returnStrategy
                                    (expr: Fable.Expr): Statement array =
        match expr with
        | Fable.Unresolved e ->
            addError com [] e.Range "Unexpected unresolved expression"
            [||]

        | Fable.TypeCast(e, t, tag) ->
            [|transformCast com ctx t tag e |> resolveExpr t returnStrategy|]

        | Fable.Curry(e, arity, t, r) ->
            [|transformCurry com ctx r e arity |> resolveExpr t returnStrategy|]

        | Fable.Value(kind, r) ->
            [|transformValue com ctx r kind |> resolveExpr kind.Type returnStrategy|]

        | Fable.IdentExpr id ->
            [|identAsExpr id |> resolveExpr id.Type returnStrategy|]

        | Fable.Import({ Selector = selector; Path = path }, t, r) ->
            [|transformImport com ctx r selector path |> resolveExpr t returnStrategy|]

        | Fable.Test(expr, kind, range) ->
            [|transformTest com ctx range kind expr |> resolveExpr Fable.Boolean returnStrategy|]

        | Fable.Lambda(arg, body, name) ->
            [|transformFunctionWithAnnotations com ctx name [arg] body
                |> makeArrowFunctionExpression name
                |> resolveExpr expr.Type returnStrategy|]

        | Fable.Delegate(args, body, name) ->
            [|transformFunctionWithAnnotations com ctx name args body
                |> makeArrowFunctionExpression name
                |> resolveExpr expr.Type returnStrategy|]

        | Fable.ObjectExpr (members, t, baseCall) ->
            [|transformObjectExpr com ctx members baseCall |> resolveExpr t returnStrategy|]

        | Fable.Call(callee, info, typ, range) ->
            transformCallAsStatements com ctx range typ returnStrategy callee info

        | Fable.CurriedApply(callee, args, typ, range) ->
            transformCurriedApplyAsStatements com ctx range typ returnStrategy callee args

        | Fable.Emit(info, t, range) ->
            let e = transformEmit com ctx range info
            if info.IsJsStatement then
                [|ExpressionStatement(e)|] // Ignore the return strategy
            else [|resolveExpr t returnStrategy e|]

        | Fable.Operation(kind, t, range) ->
            [|transformOperation com ctx range kind |> resolveExpr t returnStrategy|]

        | Fable.Get(expr, kind, t, range) ->
            [|transformGet com ctx range t expr kind |> resolveExpr t returnStrategy|]

        | Fable.Let(ident, value, body) ->
            let binding = transformBindingAsStatements com ctx ident value
            Array.append binding (transformAsStatements com ctx returnStrategy body)

        | Fable.LetRec(bindings, body) ->
            let bindings = bindings |> Seq.collect (fun (i, v) -> transformBindingAsStatements com ctx i v) |> Seq.toArray
            Array.append bindings (transformAsStatements com ctx returnStrategy body)

        | Fable.Set(expr, kind, value, range) ->
            [|transformSet com ctx range expr value kind |> resolveExpr expr.Type returnStrategy|]

        | Fable.IfThenElse(guardExpr, thenExpr, elseExpr, r) ->
            let asStatement =
                match returnStrategy with
                | None | Some ReturnUnit -> true
                | Some(Target _) -> true // Compile as statement so values can be bound
                | Some(Assign _) -> (isJsStatement ctx false thenExpr) || (isJsStatement ctx false elseExpr)
                | Some Return ->
                    Option.isSome ctx.TailCallOpportunity
                    || (isJsStatement ctx false thenExpr) || (isJsStatement ctx false elseExpr)
            if asStatement then
                transformIfStatement com ctx r returnStrategy guardExpr thenExpr elseExpr
            else
                let guardExpr' = transformAsExpr com ctx guardExpr
                let thenExpr' = transformAsExpr com ctx thenExpr
                let elseExpr' = transformAsExpr com ctx elseExpr
                [|Expression.conditionalExpression(guardExpr', thenExpr', elseExpr', ?loc=r) |> resolveExpr thenExpr.Type returnStrategy|]

        | Fable.Sequential statements ->
            let lasti = (List.length statements) - 1
            statements |> List.mapiToArray (fun i statement ->
                let ret = if i < lasti then None else returnStrategy
                com.TransformAsStatements(ctx, ret, statement))
            |> Array.concat

        | Fable.TryCatch (body, catch, finalizer, r) ->
            transformTryCatch com ctx r returnStrategy (body, catch, finalizer)

        | Fable.DecisionTree(expr, targets) ->
            transformDecisionTreeAsStatements com ctx returnStrategy targets expr

        | Fable.DecisionTreeSuccess(idx, boundValues, _) ->
            transformDecisionTreeSuccessAsStatements com ctx returnStrategy idx boundValues

        | Fable.WhileLoop(TransformExpr com ctx guard, body, range) ->
            [|Statement.whileStatement(guard, transformBlock com ctx None body, ?loc=range)|]

        | Fable.ForLoop (var, TransformExpr com ctx start, TransformExpr com ctx limit, body, isUp, range) ->
            let op1, op2 =
                if isUp
                then BinaryOperator.BinaryLessOrEqual, UpdateOperator.UpdatePlus
                else BinaryOperator.BinaryGreaterOrEqual, UpdateOperator.UpdateMinus

            [|Statement.forStatement(
                transformBlock com ctx None body,
                start |> varDeclaration (typedIdent com ctx var |> Pattern.Identifier) true,
                Expression.binaryExpression(op1, identAsExpr var, limit),
                Expression.updateExpression(op2, false, identAsExpr var), ?loc=range)|]

    let transformFunction com ctx name (args: Fable.Ident list) (body: Fable.Expr): Pattern array * BlockStatement =
        let tailcallChance =
            Option.map (fun name ->
                NamedTailCallOpportunity(com, ctx, name, args) :> ITailCallOpportunity) name
        let args = discardUnitArg args
        let declaredVars = ResizeArray()
        let mutable isTailCallOptimized = false
        let ctx =
            { ctx with TailCallOpportunity = tailcallChance
                       HoistVars = fun ids -> declaredVars.AddRange(ids); true
                       OptimizeTailCall = fun () -> isTailCallOptimized <- true }
        let body =
            if body.Type = Fable.Unit then
                transformBlock com ctx (Some ReturnUnit) body
            elif isJsStatement ctx (Option.isSome tailcallChance) body then
                transformBlock com ctx (Some Return) body
            else
                transformAsExpr com ctx body |> wrapExprInBlockWithReturn
        let args, body =
            match isTailCallOptimized, tailcallChance with
            | true, Some tc ->
                // Replace args, see NamedTailCallOpportunity constructor
                let args' =
                    List.zip args tc.Args
                    |> List.map (fun (id, tcArg) ->
                        makeTypedIdent id.Type tcArg |> typedIdent com ctx)
                let varDecls =
                    List.zip args tc.Args
                    |> List.map (fun (id, tcArg) ->
                        id |> typedIdent com ctx, Some (Expression.identifier(tcArg)))
                    |> multiVarDeclaration Const

                let body = Array.append [|varDecls|] body.Body
                // Make sure we don't get trapped in an infinite loop, see #1624
                let body = BlockStatement(Array.append body [|Statement.breakStatement()|])
                args', Statement.labeledStatement(Identifier.identifier(tc.Label), Statement.whileStatement(Expression.booleanLiteral(true), body))
                |> Array.singleton |> BlockStatement
            | _ -> args |> List.map (typedIdent com ctx), body
        let body =
            if declaredVars.Count = 0 then body
            else
                let varDeclStatement = multiVarDeclaration Let [for v in declaredVars -> typedIdent com ctx v, None]
                BlockStatement(Array.append [|varDeclStatement|] body.Body)
        args |> List.mapToArray Pattern.Identifier, body

    let declareEntryPoint _com _ctx (funcExpr: Expression) =
        let argv = emitExpression None "typeof process === 'object' ? process.argv.slice(2) : []" []
        let main = Expression.callExpression(funcExpr, [|argv|])
        // Don't exit the process after leaving main, as there may be a server running
        // ExpressionStatement(emitExpression funcExpr.loc "process.exit($0)" [main], ?loc=funcExpr.loc)
        PrivateModuleDeclaration(ExpressionStatement(main))

    let declareModuleMember isPublic membName isMutable (expr: Expression) =
        let membName' = Pattern.identifier(membName)
        let membName = Identifier.identifier(membName)
        let decl: Declaration =
            match expr with
            | ClassExpression(body, _id, superClass, implements, superTypeParameters, typeParameters, _loc) ->
                Declaration.classDeclaration(
                    body,
                    ?id = Some membName,
                    ?superClass = superClass,
                    ?superTypeParameters = superTypeParameters,
                    ?typeParameters = typeParameters,
                    ?implements = implements)
            | FunctionExpression(_, parameters, body, returnType, typeParameters, _) ->
                Declaration.functionDeclaration(
                    parameters, body, membName,
                    ?returnType = returnType,
                    ?typeParameters = typeParameters)
            | _ -> varDeclaration membName' isMutable expr |> Declaration.VariableDeclaration
        if not isPublic then PrivateModuleDeclaration(decl |> Declaration)
        else ExportNamedDeclaration(decl)

    let makeEntityTypeParamDecl (com: IBabelCompiler) _ctx (ent: Fable.Entity) =
        if com.Options.Language = TypeScript then
            getEntityGenParams ent |> makeTypeParamDecl
        else
            None

    let getClassImplements com ctx (ent: Fable.Entity) =
        let mkNative genArgs typeName =
            let id = Identifier.identifier(typeName)
            let typeParamInst = makeGenTypeParamInst com ctx genArgs
            ClassImplements.classImplements(id, ?typeParameters=typeParamInst) |> Some
//        let mkImport genArgs moduleName typeName =
//            let id = makeImportTypeId com ctx moduleName typeName
//            let typeParamInst = makeGenTypeParamInst com ctx genArgs
//            ClassImplements(id, ?typeParameters=typeParamInst) |> Some
        ent.AllInterfaces |> Seq.choose (fun ifc ->
            match ifc.Entity.FullName with
            | "Fable.Core.JS.Set`1" -> mkNative ifc.GenericArgs "Set"
            | "Fable.Core.JS.Map`2" -> mkNative ifc.GenericArgs "Map"
            | _ -> None
        )

    let getUnionFieldsAsIdents (_com: IBabelCompiler) _ctx (_ent: Fable.Entity) =
        let tagId = makeTypedIdent (Fable.Number Int32) "tag"
        let fieldsId = makeTypedIdent (Fable.Array Fable.Any) "fields"
        [| tagId; fieldsId |]

    let getEntityFieldsAsIdents _com (ent: Fable.Entity) =
        ent.FSharpFields
        |> Seq.map (fun field ->
            let name = field.Name |> Naming.sanitizeIdentForbiddenChars |> Naming.checkJsKeywords
            let typ = field.FieldType
            let id: Fable.Ident = { makeTypedIdent typ name with IsMutable = field.IsMutable }
            id)
        |> Seq.toArray

    let getEntityFieldsAsProps (com: IBabelCompiler) ctx (ent: Fable.Entity) =
        if ent.IsFSharpUnion then
            getUnionFieldsAsIdents com ctx ent
            |> Array.map (fun id ->
                let prop = identAsExpr id
                let ta = typeAnnotation com ctx id.Type
                ObjectTypeProperty.objectTypeProperty(prop, ta))
        else
            ent.FSharpFields
            |> Seq.map (fun field ->
                let prop, computed = memberFromName field.Name
                let ta = typeAnnotation com ctx field.FieldType
                let isStatic = if field.IsStatic then Some true else None
                ObjectTypeProperty.objectTypeProperty(prop, ta, computed_=computed, ?``static``=isStatic))
            |> Seq.toArray

    let declareClassType (com: IBabelCompiler) ctx (ent: Fable.Entity) entName (consArgs: Pattern[]) (consBody: BlockStatement) (baseExpr: Expression option) classMembers =
        let typeParamDecl = makeEntityTypeParamDecl com ctx ent
        let implements =
            if com.Options.Language = TypeScript then
                let implements = Util.getClassImplements com ctx ent |> Seq.toArray
                if Array.isEmpty implements then None else Some implements
            else None
        let classCons = makeClassConstructor consArgs consBody
        let classFields =
            if com.Options.Language = TypeScript then
                getEntityFieldsAsProps com ctx ent
                |> Array.map (fun (ObjectTypeProperty(key, value, _, _, ``static``, _, _, _)) ->
                    let ta = value |> TypeAnnotation |> Some
                    ClassMember.classProperty(key, ``static``=``static``, ?typeAnnotation=ta))
            else Array.empty
        let classMembers = Array.append [| classCons |] classMembers
        let classBody = ClassBody.classBody([| yield! classFields; yield! classMembers |])
        let classExpr = Expression.classExpression(classBody, ?superClass=baseExpr, ?typeParameters=typeParamDecl, ?implements=implements)
        classExpr |> declareModuleMember ent.IsPublic entName false

    let declareType (com: IBabelCompiler) ctx (ent: Fable.Entity) entName (consArgs: Pattern[]) (consBody: BlockStatement) baseExpr classMembers: ModuleDeclaration list =
        let typeDeclaration = declareClassType com ctx ent entName consArgs consBody baseExpr classMembers
        if com.Options.NoReflection then
            [typeDeclaration]
        else
            let reflectionDeclaration =
                let ta =
                    if com.Options.Language = TypeScript then
                        makeImportTypeAnnotation com ctx [] "Reflection" "TypeInfo"
                        |> TypeAnnotation |> Some
                    else None
                let genArgs = Array.init (ent.GenericParameters.Length) (fun i -> "gen" + string i |> makeIdent)
                let generics = genArgs |> Array.map identAsExpr
                let body = transformReflectionInfo com ctx None ent generics
                let args = genArgs |> Array.map (fun x -> Pattern.identifier(x.Name, ?typeAnnotation=ta))
                let returnType = ta
                makeFunctionExpression None (args, body, returnType, None)
                |> declareModuleMember ent.IsPublic (entName + Naming.reflectionSuffix) false
            [typeDeclaration; reflectionDeclaration]

    let transformModuleFunction (com: IBabelCompiler) ctx (info: Fable.MemberInfo) (membName: string) args body =
        let args, body, returnType, typeParamDecl =
            getMemberArgsAndBody com ctx (NonAttached membName) info.HasSpread args body
        let expr = Expression.functionExpression(args, body, ?returnType=returnType, ?typeParameters=typeParamDecl)
        info.Attributes
        |> Seq.exists (fun att -> att.Entity.FullName = Atts.entryPoint)
        |> function
        | true -> declareEntryPoint com ctx expr
        | false -> declareModuleMember info.IsPublic membName false expr

    let transformAction (com: IBabelCompiler) ctx expr =
        let statements = transformAsStatements com ctx None expr
        let hasVarDeclarations =
            statements |> Array.exists (function
                | Declaration(Declaration.VariableDeclaration(_)) -> true
                | _ -> false)
        if hasVarDeclarations then
            [ Expression.callExpression(Expression.functionExpression([||], BlockStatement(statements)), [||])
              |> ExpressionStatement |> PrivateModuleDeclaration ]
        else statements |> Array.mapToList (fun x -> PrivateModuleDeclaration(x))

    let transformAttachedProperty (com: IBabelCompiler) ctx (memb: Fable.MemberDecl) =
        let isStatic = not memb.Info.IsInstance
        let kind = if memb.Info.IsGetter then ClassGetter else ClassSetter
        let args, body, _returnType, _typeParamDecl =
            getMemberArgsAndBody com ctx (Attached isStatic) false memb.Args memb.Body
        let key, computed = memberFromName memb.Name
        ClassMember.classMethod(kind, key, args, body, computed_=computed, ``static``=isStatic)
        |> Array.singleton

    let transformAttachedMethod (com: IBabelCompiler) ctx (memb: Fable.MemberDecl) =
        let isStatic = not memb.Info.IsInstance
        let makeMethod name args body =
            let key, computed = memberFromName name
            ClassMember.classMethod(ClassFunction, key, args, body, computed_=computed, ``static``=isStatic)
        let args, body, _returnType, _typeParamDecl =
            getMemberArgsAndBody com ctx (Attached isStatic) memb.Info.HasSpread memb.Args memb.Body
        [|
            yield makeMethod memb.Name args body
            if memb.Info.IsEnumerator then
                yield makeMethod "Symbol.iterator" [||] (enumerator2iterator com ctx)
        |]

    let transformUnion (com: IBabelCompiler) ctx (ent: Fable.Entity) (entName: string) classMembers =
        let fieldIds = getUnionFieldsAsIdents com ctx ent
        let args =
            [| typedIdent com ctx fieldIds.[0] |> Pattern.Identifier
               typedIdent com ctx fieldIds.[1] |> Pattern.Identifier |> restElement |]
        let body =
            BlockStatement([|
                yield callSuperAsStatement []
                yield! fieldIds |> Array.map (fun id ->
                    let left = get None thisExpr id.Name
                    let right =
                        match id.Type with
                        | Fable.Number _ ->
                            Expression.binaryExpression(BinaryOrBitwise, identAsExpr id, Expression.numericLiteral(0.))
                        | _ -> identAsExpr id
                    assign None left right |> ExpressionStatement)
            |])
        let cases =
            let body =
                ent.UnionCases
                |> Seq.map (getUnionCaseName >> makeStrConst)
                |> Seq.toList
                |> makeArray com ctx
                |>  Statement.returnStatement
                |> Array.singleton
                |> BlockStatement
            ClassMember.classMethod(ClassFunction, Expression.identifier("cases"), [||], body)

        let baseExpr = libValue com ctx "Types" "Union" |> Some
        let classMembers = Array.append [|cases|] classMembers
        declareType com ctx ent entName args body baseExpr classMembers

    let transformClassWithCompilerGeneratedConstructor (com: IBabelCompiler) ctx (ent: Fable.Entity) (entName: string) classMembers =
        let fieldIds = getEntityFieldsAsIdents com ent
        let args = fieldIds |> Array.map identAsExpr
        let baseExpr =
            if ent.IsFSharpExceptionDeclaration
            then libValue com ctx "Types" "FSharpException" |> Some
            elif ent.IsFSharpRecord || ent.IsValueType
            then libValue com ctx "Types" "Record" |> Some
            else None
        let body =
            BlockStatement([|
                if Option.isSome baseExpr then
                    yield callSuperAsStatement []
                yield! ent.FSharpFields |> Seq.mapi (fun i field ->
                    let left = get None thisExpr field.Name
                    let right = wrapIntExpression field.FieldType args.[i]
                    assign None left right |> ExpressionStatement)
                |> Seq.toArray
            |])
        let typedPattern x = typedIdent com ctx x
        let args = fieldIds |> Array.map (typedPattern >> Pattern.Identifier)
        declareType com ctx ent entName args body baseExpr classMembers

    let transformClassWithImplicitConstructor (com: IBabelCompiler) ctx (classEnt: Fable.Entity) (classDecl: Fable.ClassDecl) classMembers (cons: Fable.MemberDecl) =
        let classIdent = Expression.identifier(classDecl.Name)
        let consArgs, consBody, returnType, typeParamDecl =
            getMemberArgsAndBody com ctx ClassConstructor cons.Info.HasSpread cons.Args cons.Body

        let returnType, typeParamDecl =
            // change constructor's return type from void to entity type
            if com.Options.Language = TypeScript then
                let genParams = getEntityGenParams classEnt
                let returnType = getGenericTypeAnnotation com ctx classDecl.Name genParams
                let typeParamDecl = makeTypeParamDecl genParams |> mergeTypeParamDecls typeParamDecl
                returnType, typeParamDecl
            else
                returnType, typeParamDecl

        let exposedCons =
            let argExprs = consArgs |> Array.map (fun p -> Expression.identifier(p.Name))
            let exposedConsBody = Expression.newExpression(classIdent, argExprs)
            makeFunctionExpression None (consArgs, exposedConsBody, returnType, typeParamDecl)

        let baseExpr, consBody =
            classDecl.BaseCall
            |> extractBaseExprFromBaseCall com ctx classEnt.BaseType
            |> Option.orElseWith (fun () ->
                if classEnt.IsValueType then Some(libValue com ctx "Types" "Record", [])
                else None)
            |> Option.map (fun (baseExpr, baseArgs) ->
                let consBody =
                    consBody.Body
                    |> Array.append [|callSuperAsStatement baseArgs|]
                    |> BlockStatement
                Some baseExpr, consBody)
            |> Option.defaultValue (None, consBody)

        [
            yield! declareType com ctx classEnt classDecl.Name consArgs consBody baseExpr classMembers
            yield declareModuleMember cons.Info.IsPublic cons.Name false exposedCons
        ]

    let rec transformDeclaration (com: IBabelCompiler) ctx decl =
        let withCurrentScope ctx (usedNames: Set<string>) f =
            let ctx = { ctx with UsedNames = { ctx.UsedNames with CurrentDeclarationScope = HashSet usedNames } }
            let result = f ctx
            ctx.UsedNames.DeclarationScopes.UnionWith(ctx.UsedNames.CurrentDeclarationScope)
            result

        match decl with
        | Fable.ActionDeclaration decl ->
            withCurrentScope ctx decl.UsedNames <| fun ctx ->
                transformAction com ctx decl.Body

        | Fable.MemberDeclaration decl ->
            withCurrentScope ctx decl.UsedNames <| fun ctx ->
                let decls =
                    if decl.Info.IsValue then
                        let value = transformAsExpr com ctx decl.Body
                        [declareModuleMember decl.Info.IsPublic decl.Name decl.Info.IsMutable value]
                    else
                        [transformModuleFunction com ctx decl.Info decl.Name decl.Args decl.Body]

                if decl.ExportDefault then
                    decls @ [ExportDefaultDeclaration(Choice2Of2(Expression.identifier(decl.Name)))]
                else decls

        | Fable.ClassDeclaration decl ->
            let entRef = decl.Entity
            let ent = com.GetEntity(entRef)
            if ent.IsInterface then
                // TODO: Add type annotation for Typescript
                []
            else
                let classMembers =
                    decl.AttachedMembers
                    |> List.toArray
                    |> Array.collect (fun memb ->
                        withCurrentScope ctx memb.UsedNames <| fun ctx ->
                            if memb.Info.IsGetter || memb.Info.IsSetter then
                                transformAttachedProperty com ctx memb
                            else
                                transformAttachedMethod com ctx memb)

                match decl.Constructor with
                | Some cons ->
                    withCurrentScope ctx cons.UsedNames <| fun ctx ->
                        transformClassWithImplicitConstructor com ctx ent decl classMembers cons
                | None ->
                    if ent.IsFSharpUnion then transformUnion com ctx ent decl.Name classMembers
                    else transformClassWithCompilerGeneratedConstructor com ctx ent decl.Name classMembers

    let transformImports (imports: Import seq): ModuleDeclaration list =
        let statefulImports = ResizeArray()
        imports |> Seq.map (fun import ->
            let specifier =
                import.LocalIdent
                |> Option.map (fun localId ->
                    let localId = Identifier.identifier(localId)
                    match import.Selector with
                    | "*" -> ImportNamespaceSpecifier(localId)
                    | "default" | "" -> ImportDefaultSpecifier(localId)
                    | memb -> ImportMemberSpecifier(localId, Identifier.identifier(memb)))
            import.Path, specifier)
        |> Seq.groupBy fst
        |> Seq.collect (fun (path, specifiers) ->
            let mems, defs, alls =
                (([], [], []), Seq.choose snd specifiers)
                ||> Seq.fold (fun (mems, defs, alls) x ->
                    match x with
                    | ImportNamespaceSpecifier(_) -> mems, defs, x::alls
                    | ImportDefaultSpecifier(_) -> mems, x::defs, alls
                    | _ -> x::mems, defs, alls)
            // We used to have trouble when mixing member, default and namespace imports,
            // issue an import statement for each kind just in case
            [mems; defs; alls] |> List.choose (function
                | [] -> None
                | specifiers ->
                    ImportDeclaration(List.toArray specifiers, StringLiteral.stringLiteral(path))
                    |> Some)
            |> function
                | [] ->
                    // If there are no specifiers, this is just an import for side effects,
                    // put it after the other ones to match standard JS practices, see #2228
                    ImportDeclaration([||], StringLiteral.stringLiteral(path))
                    |> statefulImports.Add
                    []
                | decls -> decls
            )
        |> fun staticImports -> [
            yield! staticImports
            yield! statefulImports
        ]

    let getIdentForImport (ctx: Context) (path: string) (selector: string) =
        if System.String.IsNullOrEmpty selector then None
        else
            match selector with
            | "*" | "default" -> Path.GetFileNameWithoutExtension(path).Replace("-", "_")
            | _ -> selector
            |> getUniqueNameInRootScope ctx
            |> Some

module Compiler =
    open Util

    type BabelCompiler (com: Compiler) =
        let onlyOnceWarnings = HashSet<string>()
        let imports = Dictionary<string,Import>()

        interface IBabelCompiler with
            member _.WarnOnlyOnce(msg, ?range) =
                if onlyOnceWarnings.Add(msg) then
                    addWarning com [] range msg

            member _.GetImportExpr(ctx, selector, path, r) =
                let cachedName = path + "::" + selector
                match imports.TryGetValue(cachedName) with
                | true, i ->
                    match i.LocalIdent with
                    | Some localIdent -> Expression.identifier(localIdent)
                    | None -> Expression.nullLiteral()
                | false, _ ->
                    let localId = getIdentForImport ctx path selector
                    let i =
                      { Selector =
                            if selector = Naming.placeholder then
                                     "`importMember` must be assigned to a variable"
                                     |> addError com [] r; selector
                            else selector
                        Path = path
                        LocalIdent = localId }
                    imports.Add(cachedName, i)
                    match localId with
                    | Some localId -> Expression.identifier(localId)
                    | None -> Expression.nullLiteral()
            member _.GetAllImports() = imports.Values :> _
            member bcom.TransformAsExpr(ctx, e) = transformAsExpr bcom ctx e
            member bcom.TransformAsStatements(ctx, ret, e) = transformAsStatements bcom ctx ret e
            member bcom.TransformFunction(ctx, name, args, body) = transformFunction bcom ctx name args body
            member bcom.TransformImport(ctx, selector, path) = transformImport bcom ctx None selector path

        interface Compiler with
            member _.Options = com.Options
            member _.Plugins = com.Plugins
            member _.LibraryDir = com.LibraryDir
            member _.CurrentFile = com.CurrentFile
            member _.OutputDir = com.OutputDir
            member _.ProjectFile = com.ProjectFile
            member _.IsPrecompilingInlineFunction = com.IsPrecompilingInlineFunction
            member _.WillPrecompileInlineFunction(file) = com.WillPrecompileInlineFunction(file)
            member _.TryGetEntity(fullName) = com.TryGetEntity(fullName)
            member _.GetImplementationFile(fileName) = com.GetImplementationFile(fileName)
            member _.GetRootModule(fileName) = com.GetRootModule(fileName)
            member _.GetInlineExpr(fullName) = com.GetInlineExpr(fullName)
            member _.AddWatchDependency(fileName) = com.AddWatchDependency(fileName)
            member _.AddLog(msg, severity, ?range, ?fileName:string, ?tag: string) =
                com.AddLog(msg, severity, ?range=range, ?fileName=fileName, ?tag=tag)

    let makeCompiler com = BabelCompiler(com)

    let transformFile (com: Compiler) (file: Fable.File) =
        let com = makeCompiler com :> IBabelCompiler
        let declScopes =
            let hs = HashSet()
            for decl in file.Declarations do
                hs.UnionWith(decl.UsedNames)
            hs

        let ctx =
          { File = file
            UsedNames = { RootScope = HashSet file.UsedNamesInRootScope
                          DeclarationScopes = declScopes
                          CurrentDeclarationScope = Unchecked.defaultof<_> }
            DecisionTargets = []
            HoistVars = fun _ -> false
            TailCallOpportunity = None
            OptimizeTailCall = fun () -> ()
            ScopedTypeParams = Set.empty }
        let rootDecls = List.collect (transformDeclaration com ctx) file.Declarations
        let importDecls = com.GetAllImports() |> transformImports
        let body = importDecls @ rootDecls |> List.toArray
        Program(body)
