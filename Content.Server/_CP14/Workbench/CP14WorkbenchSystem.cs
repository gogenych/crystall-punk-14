using Content.Server.DoAfter;
using Content.Server.Popups;
using Content.Server.Stack;
using Content.Shared._CP14.Workbench;
using Content.Shared._CP14.Workbench.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Content.Shared.Verbs;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._CP14.Workbench;

public sealed partial class CP14WorkbenchSystem : SharedCP14WorkbenchSystem
{
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly DoAfterSystem _doAfter = default!;
    [Dependency] private readonly StackSystem _stack = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly UserInterfaceSystem _userInterface = default!;

    private EntityQuery<MetaDataComponent> _metaQuery;
    private EntityQuery<StackComponent> _stackQuery;

    // Why not in component? Why?
    private const float WorkbenchRadius = 0.5f;

    public override void Initialize()
    {
        base.Initialize();

        _metaQuery = GetEntityQuery<MetaDataComponent>();
        _stackQuery = GetEntityQuery<StackComponent>();

        SubscribeLocalEvent<CP14WorkbenchComponent, BeforeActivatableUIOpenEvent>(OnBeforeUIOpen);
        SubscribeLocalEvent<CP14WorkbenchComponent, CP14WorkbenchUiCraftMessage>(OnCraft);

        SubscribeLocalEvent<CP14WorkbenchComponent, GetVerbsEvent<InteractionVerb>>(OnInteractionVerb);
        SubscribeLocalEvent<CP14WorkbenchComponent, CP14CraftDoAfterEvent>(OnCraftFinished);
    }

    private void OnBeforeUIOpen(Entity<CP14WorkbenchComponent> ent, ref BeforeActivatableUIOpenEvent args)
    {
        UpdateUIRecipes(ent);
    }

    private void OnInteractionVerb(Entity<CP14WorkbenchComponent> ent, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Hands is null)
            return;

        var placedEntities = _lookup.GetEntitiesInRange(Transform(ent).Coordinates, WorkbenchRadius);

        var user = args.User;
        foreach (var craftProto in ent.Comp.Recipes)
        {
            if (!_proto.TryIndex(craftProto, out var craft))
                continue;

            if (!_proto.TryIndex(craft.Result, out var result))
                continue;

            args.Verbs.Add(new()
            {
                Act = () =>
                {
                    StartCraft(ent, user, craft);
                },
                Text = result.Name,
                Message = GetCraftRecipeMessage(result.Description, craft),
                Category = VerbCategory.CP14Craft,
                Disabled = !CanCraftRecipe(craft, placedEntities),
            });
        }
    }

    // TODO: Replace Del to QueueDel when it's will be works with events
    private void OnCraftFinished(Entity<CP14WorkbenchComponent> ent, ref CP14CraftDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        if (!_proto.TryIndex(args.Recipe, out var recipe))
            return;

        var placedEntities = _lookup.GetEntitiesInRange(Transform(ent).Coordinates, WorkbenchRadius);

        if (!CanCraftRecipe(recipe, placedEntities))
        {
            _popup.PopupEntity(Loc.GetString("cp14-workbench-no-resource"), ent, args.User);
            return;
        }

        foreach (var requiredIngredient  in recipe.Entities)
        {
            var requiredCount = requiredIngredient.Value;
            foreach (var placedEntity in placedEntities)
            {
                var placedProto = MetaData(placedEntity).EntityPrototype?.ID;
                if (placedProto != null && placedProto == requiredIngredient.Key && requiredCount > 0)
                {
                    requiredCount--;
                    Del(placedEntity);
                }
            }
        }

        foreach (var requiredStack in recipe.Stacks)
        {
            var requiredCount = requiredStack.Value;
            foreach (var placedEntity in placedEntities)
            {
                if (!_stackQuery.TryGetComponent(placedEntity, out var stack))
                    continue;

                if (stack.StackTypeId != requiredStack.Key)
                    continue;

                var count = (int)MathF.Min(requiredCount, stack.Count);

                if (stack.Count - count <= 0)
                    Del(placedEntity);
                else
                    _stack.SetCount(placedEntity, stack.Count - count, stack);

                requiredCount -= count;
            }
        }

        Spawn(_proto.Index(args.Recipe).Result, Transform(ent).Coordinates);
        UpdateUIRecipes(ent);
        args.Handled = true;
    }

    private void StartCraft(Entity<CP14WorkbenchComponent> workbench, EntityUid user, CP14WorkbenchRecipePrototype recipe)
    {
        var craftDoAfter = new CP14CraftDoAfterEvent
        {
            Recipe = recipe.ID,
        };

        var doAfterArgs = new DoAfterArgs(EntityManager,
            user,
            recipe.CraftTime * workbench.Comp.CraftSpeed,
            craftDoAfter,
            workbench,
            workbench)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        };

        _doAfter.TryStartDoAfter(doAfterArgs);
        _audio.PlayPvs(recipe.OverrideCraftSound ?? workbench.Comp.CraftSound, workbench);
    }

    private List<CP14WorkbenchRecipePrototype> GetPossibleCrafts(Entity<CP14WorkbenchComponent> workbench, HashSet<EntityUid> ingrediEnts)
    {
        List<CP14WorkbenchRecipePrototype> result = new();

        if (ingrediEnts.Count == 0)
            return result;

        foreach (var recipeProto in workbench.Comp.Recipes)
        {
            var recipe = _proto.Index(recipeProto);

            if (CanCraftRecipe(recipe, ingrediEnts))
            {
                result.Add(recipe);
            }
        }

        return result;
    }

    private bool CanCraftRecipe(CP14WorkbenchRecipePrototype recipe, HashSet<EntityUid> entities)
    {
        var indexedIngredients = IndexIngredients(entities);
        foreach (var requiredIngredient  in recipe.Entities)
        {
            if (!indexedIngredients.TryGetValue(requiredIngredient.Key, out var availableQuantity) ||
                availableQuantity < requiredIngredient.Value)
                return false;
        }

        foreach (var (key, value) in recipe.Stacks)
        {
            var count = 0;
            foreach (var ent in entities)
            {
                if (_stackQuery.TryGetComponent(ent, out var stack))
                {
                    if (stack.StackTypeId != key)
                        continue;

                    count += stack.Count;
                }
            }

            if (count < value)
                return false;
        }
        return true;
    }

    private string GetCraftRecipeMessage(string desc, CP14WorkbenchRecipePrototype recipe)
    {
        var result = desc + "\n \n" + Loc.GetString("cp14-workbench-recipe-list")+ "\n";

        foreach (var pair in recipe.Entities)
        {
            var proto = _proto.Index(pair.Key);
            result += $"{proto.Name} x{pair.Value}\n";
        }

        foreach (var pair in recipe.Stacks)
        {
            var proto = _proto.Index(pair.Key);
            result += $"{proto.Name} x{pair.Value}\n";
        }

        return result;
    }

    private Dictionary<EntProtoId, int> IndexIngredients(HashSet<EntityUid> ingredients)
    {
        var indexedIngredients = new Dictionary<EntProtoId, int>();

        foreach (var ingredient in ingredients)
        {
            var protoId = _metaQuery.GetComponent(ingredient).EntityPrototype?.ID;
            if (protoId == null)
                continue;

            if (indexedIngredients.ContainsKey(protoId))
                indexedIngredients[protoId]++;
            else
                indexedIngredients[protoId] = 1;
        }
        return indexedIngredients;
    }
}
