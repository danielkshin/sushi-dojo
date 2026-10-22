using System;
using System.Collections;
using System.Collections.Generic;
using SushiDojo.Ingredients;
using UnityEngine;

namespace SushiDojo.Cards
{
    [RequireComponent(typeof(RectTransform))]
    public class RecipeQueueView : MonoBehaviour
    {
        [SerializeField] RecipeCardUI cardPrefab;
        [SerializeField] RecipeData[] recipeDeck = Array.Empty<RecipeData>();
        [SerializeField, Min(0f)] float cardSpacing = 160f;
        [SerializeField, Range(0.1f, 1f)] float cardScale = 0.65f;
        [SerializeField, Min(0.01f)] float animationDuration = 0.35f;
        [SerializeField, Min(0f)] float exitDistance = 190f;

        [Header("Completion")]
        [Tooltip("Cooking effect on the board. The card waits for it. Empty = finds one in the scene; none = old instant behavior.")]
        [SerializeField] MergePresentation mergePresentation;
        [Tooltip("Card scale peak during the 'pop' once the dish is done (multiplier).")]
        [SerializeField, Min(1f)] float cardPopScale = 1.15f;
        [SerializeField, Min(0.01f)] float cardPopDuration = 0.2f;
        [Tooltip("The completed card fades away over this long before the next card moves up.")]
        [SerializeField, Min(0.01f)] float cardFadeDuration = 0.45f;

        RecipeCardUI topCard;
        RecipeCardUI nextCard;
        [SerializeField] IngredientPlacementZone placementZone;
        readonly List<IngredientData> submittedIngredients = new();
        RecipeData[] catalog = Array.Empty<RecipeData>();
        RecipeData currentRecipe;
        RecipeData nextRecipe;
        System.Random random;
        bool isAnimating;

        public event Action<RecipeData> RecipeCompleted;
        /// <summary>Fired when the visible current/next recipes change (after the opening deal or a completion).</summary>
        public event Action RecipeQueueChanged;

        public RecipeData CurrentRecipe => currentRecipe;
        public RecipeData NextRecipe => nextRecipe;

        /// <summary>The full recipe list. Completed recipes stay in it and can be dealt again.</summary>
        public IReadOnlyList<RecipeData> DeckRecipes => catalog;

        void Awake()
        {
            Canvas canvas = GetComponentInParent<Canvas>()?.rootCanvas;
            if (canvas != null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.worldCamera = null;
            }

            if (recipeDeck == null)
            {
                Debug.LogError($"{nameof(RecipeQueueView)} requires a recipe deck.", this);
                enabled = false;
                return;
            }

            if (cardPrefab == null)
            {
                Debug.LogError($"{nameof(RecipeQueueView)} requires a recipe card prefab.", this);
                enabled = false;
                return;
            }

            for (int i = 0; i < recipeDeck.Length; i++)
            {
                if (recipeDeck[i] != null) continue;

                Debug.LogError($"{nameof(RecipeQueueView)} has an empty recipe deck entry at index {i}.", this);
                enabled = false;
                return;
            }

            catalog = (RecipeData[])recipeDeck.Clone();
            random = new System.Random(Guid.NewGuid().GetHashCode());
            currentRecipe = DrawRecipe(null);
            nextRecipe = DrawRecipe(currentRecipe);
            topCard = Instantiate(cardPrefab, transform, false);
            nextCard = Instantiate(cardPrefab, transform, false);
            PositionCard(topCard, TopSlotPosition);
            PositionCard(nextCard, NextSlotPosition);
            RefreshCards();
            RecipeQueueChanged?.Invoke();
            Debug.Log($"[RecipeQueue] Ready in {gameObject.scene.name}; recipes={catalog.Length}.", this);
        }

        /// <summary>
        /// Draw one recipe from the full list. Tier percentages are 3→50%, 4→40%, 6+→10%,
        /// then uniform within the chosen tier. Empty tiers redistribute their share.
        /// <paramref name="except"/> is skipped when another recipe is available, so the two
        /// visible cards differ, while a finished recipe can be dealt again right away.
        /// </summary>
        RecipeData DrawRecipe(RecipeData except)
        {
            var pool = new List<RecipeData>(catalog.Length);
            foreach (RecipeData recipe in catalog)
            {
                if (recipe == null || recipe == except) continue;
                pool.Add(recipe);
            }

            if (pool.Count == 0)
            {
                foreach (RecipeData recipe in catalog)
                    if (recipe != null) pool.Add(recipe);
            }

            if (pool.Count == 0) return null;

            int tier = PickRecipeTier(pool, random);
            int pick = -1;
            int tierCount = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                if (RecipeTier(pool[i]) != tier) continue;
                tierCount++;
                if (random.Next(tierCount) == 0)
                    pick = i;
            }

            return pick >= 0 ? pool[pick] : pool[pool.Count - 1];
        }

        static int PickRecipeTier(List<RecipeData> remaining, System.Random random)
        {
            float w3 = 0f, w4 = 0f, w5 = 0f, w6 = 0f;
            foreach (RecipeData recipe in remaining)
            {
                switch (RecipeTier(recipe))
                {
                    case 3: w3 = 0.5f; break;
                    case 4: w4 = 0.4f; break;
                    case 5: w5 = 0f; break;
                    default: w6 = 0.1f; break;
                }
            }

            float total = w3 + w4 + w5 + w6;
            if (total <= 0f) return 3;

            double roll = random.NextDouble() * total;
            if ((roll -= w3) < 0d) return 3;
            if ((roll -= w4) < 0d) return 4;
            if ((roll -= w5) < 0d) return 5;
            return 6;
        }

        static int RecipeTier(RecipeData recipe)
        {
            int count = RecipeIngredientWeights.CountIngredients(recipe);
            if (count <= 3) return 3;
            if (count == 4) return 4;
            if (count == 5) return 5;
            return 6;
        }

        void OnEnable()
        {
            if (mergePresentation == null)
                mergePresentation = FindFirstObjectByType<MergePresentation>();
            if (placementZone == null)
                placementZone = FindFirstObjectByType<IngredientPlacementZone>();
            if (placementZone == null)
            {
                Debug.LogWarning(
                    $"{nameof(RecipeQueueView)} could not find an ingredient placement zone; " +
                    "recipes can still be completed by calling CompleteTopRecipe().",
                    this);
                return;
            }

            placementZone.AcceptingIngredients = CurrentRecipe != null && !isAnimating;
            placementZone.IngredientPlaced += OnIngredientPlaced;
            placementZone.IngredientsDiscarded += OnIngredientsDiscarded;
        }

        void OnDisable()
        {
            if (placementZone != null)
            {
                placementZone.IngredientPlaced -= OnIngredientPlaced;
                placementZone.IngredientsDiscarded -= OnIngredientsDiscarded;
            }
            submittedIngredients.Clear();
        }

        public void CompleteTopRecipe()
        {
            if (!enabled || isAnimating || currentRecipe == null) return;
            // The board ingredients are handed to the merge effect (or cleared) inside the routine.
            submittedIngredients.Clear();
            StartCoroutine(CompleteTopRecipeRoutine());
        }

        void OnIngredientPlaced(IngredientData ingredient)
        {
            if (CurrentRecipe == null || placementZone == null) return;

            if (isAnimating) return;

            submittedIngredients.Add(ingredient);
            RecipeData secondRecipe = nextRecipe;
            if (MatchesRecipe(CurrentRecipe))
            {
                CompleteTopRecipe();
                return;
            }
            if (secondRecipe != null && MatchesRecipe(secondRecipe))
            {
                submittedIngredients.Clear();
                StartCoroutine(CompleteSecondRecipeRoutine());
                return;
            }

        }

        void OnIngredientsDiscarded(int discardedIngredientCount)
        {
            submittedIngredients.Clear();
            NetworkMatchTimer timer = FindFirstObjectByType<NetworkMatchTimer>();
            if (timer != null && timer.IsMatchSettling)
                StartCoroutine(RecordWasteAfterThrow(discardedIngredientCount));
            else
                RecordWaste(discardedIngredientCount);
        }

        IEnumerator RecordWasteAfterThrow(int discardedIngredientCount)
        {
            yield return new WaitForSecondsRealtime(1f);
            RecordWaste(discardedIngredientCount);
        }

        void RecordWaste(int discardedIngredientCount)
        {
            NetworkScoreboard scoreboard = FindFirstObjectByType<NetworkScoreboard>();
            if (scoreboard != null)
                scoreboard.RecordDiscardedIngredients(discardedIngredientCount);
        }

        /// <summary>
        /// True when ingredients are on the board but no visible recipe (top or next) can still be made from them,
        /// so the only way forward is to throw the board.
        /// </summary>
        public bool IsBoardHopeless
        {
            get
            {
                if (isAnimating || submittedIngredients.Count == 0 || CurrentRecipe == null) return false;
                if (CanCompleteRecipe(CurrentRecipe)) return false;
                return nextRecipe == null || !CanCompleteRecipe(nextRecipe);
            }
        }

        bool MatchesRecipe(RecipeData recipe) =>
            submittedIngredients.Count == CountRequiredIngredients(recipe) && CanCompleteRecipe(recipe);

        bool CanCompleteRecipe(RecipeData recipe)
        {
            bool[] matched = new bool[recipe.Ingredients.Count];
            foreach (IngredientData submitted in submittedIngredients)
            {
                int matchIndex = -1;
                for (int i = 0; i < recipe.Ingredients.Count; i++)
                {
                    if (matched[i] || recipe.Ingredients[i] == null || recipe.Ingredients[i] != submitted) continue;
                    matchIndex = i;
                    break;
                }
                if (matchIndex < 0) return false;
                matched[matchIndex] = true;
            }
            return true;
        }

        IEnumerator CompleteSecondRecipeRoutine()
        {
            isAnimating = true;
            if (placementZone != null) placementZone.AcceptingIngredients = false;
            RecipeData completedRecipe = nextRecipe;
            yield return CookOnBoard(completedRecipe);
            yield return PopAndFade(nextCard);

            nextRecipe = DrawRecipe(currentRecipe);
            RefreshCards();
            if (nextRecipe != null)
            {
                PositionCard(nextCard, NextSlotPosition + Vector2.right * exitDistance);
                yield return AnimateCards((RectTransform)nextCard.transform,
                    NextSlotPosition, Vector3.one * cardScale, null, Vector2.zero, Vector3.one);
            }
            isAnimating = false;
            if (placementZone != null) placementZone.AcceptingIngredients = CurrentRecipe != null;
            NotifyRecipeCompleted(completedRecipe);
        }

        static int CountRequiredIngredients(RecipeData recipe)
        {
            int count = 0;
            foreach (IngredientData ingredient in recipe.Ingredients)
            {
                if (ingredient != null) count++;
            }

            return count;
        }

        void NotifyRecipeCompleted(RecipeData recipe)
        {
            RecipeCompleted?.Invoke(recipe);
            RecipeQueueChanged?.Invoke();
            NetworkScoreboard scoreboard = FindFirstObjectByType<NetworkScoreboard>();
            if (scoreboard != null)
                scoreboard.RecordRecipeCompleted(CountRequiredIngredients(recipe));
        }

        IEnumerator CompleteTopRecipeRoutine()
        {
            isAnimating = true;
            if (placementZone != null) placementZone.AcceptingIngredients = false;
            RecipeData completedRecipe = currentRecipe;
            RectTransform topRect = (RectTransform)topCard.transform;
            RectTransform nextRect = nextRecipe != null
                ? (RectTransform)nextCard.transform
                : null;

            // Cook first, then the finished card pops and fades, then the next card moves up.
            yield return CookOnBoard(completedRecipe);
            yield return PopAndFade(topCard);
            if (nextRect != null)
                yield return AnimateCards(nextRect, TopSlotPosition, Vector3.one * cardScale, null, Vector2.zero, Vector3.one);
            topRect.anchoredPosition = TopSlotPosition + Vector2.left * exitDistance;

            currentRecipe = nextRecipe;
            nextRecipe = DrawRecipe(currentRecipe);
            RecipeCardUI oldTopCard = topCard;
            topCard = nextCard;
            nextCard = oldTopCard;

            if (currentRecipe != null)
            {
                PositionCard(topCard, TopSlotPosition);
                if (nextRecipe != null)
                {
                    nextCard.SetData(nextRecipe);
                    nextCard.gameObject.SetActive(true);
                    PositionCard(nextCard, NextSlotPosition + Vector2.right * exitDistance);
                    yield return AnimateCards(
                        (RectTransform)nextCard.transform,
                        NextSlotPosition,
                        Vector3.one * cardScale,
                        null,
                        Vector2.zero,
                        Vector3.one);
                }
                else
                {
                    nextCard.SetData(null);
                    nextCard.gameObject.SetActive(false);
                }
            }
            else
            {
                topCard.gameObject.SetActive(false);
                nextCard.gameObject.SetActive(false);
            }

            isAnimating = false;
            if (placementZone != null) placementZone.AcceptingIngredients = CurrentRecipe != null;
            NotifyRecipeCompleted(completedRecipe);
        }

        /// <summary>Plays the board cooking effect, or just clears the board when there is none.</summary>
        IEnumerator CookOnBoard(RecipeData recipe)
        {
            if (mergePresentation != null)
                yield return mergePresentation.Play(recipe);
            else if (placementZone != null)
                placementZone.ConsumePlacedIngredients();
        }

        /// <summary>The completed card bounces once, then fades out where it is.</summary>
        IEnumerator PopAndFade(RecipeCardUI card)
        {
            RectTransform rect = (RectTransform)card.transform;
            CanvasGroup group = CardGroup(card);
            Vector3 baseScale = rect.localScale;

            for (float t = 0f; t < cardPopDuration; t += Time.unscaledDeltaTime)
            {
                float bump = Mathf.Sin(t / cardPopDuration * Mathf.PI);
                rect.localScale = baseScale * Mathf.Lerp(1f, cardPopScale, bump);
                yield return null;
            }
            rect.localScale = baseScale;

            for (float t = 0f; t < cardFadeDuration; t += Time.unscaledDeltaTime)
            {
                group.alpha = 1f - Mathf.SmoothStep(0f, 1f, t / cardFadeDuration);
                yield return null;
            }
            group.alpha = 0f;
        }

        static CanvasGroup CardGroup(RecipeCardUI card) =>
            card.TryGetComponent(out CanvasGroup group) ? group : card.gameObject.AddComponent<CanvasGroup>();

        IEnumerator AnimateCards(
            RectTransform first,
            Vector2 firstTarget,
            Vector3 firstScaleTarget,
            RectTransform second,
            Vector2 secondTarget,
            Vector3 secondScaleTarget)
        {
            Vector2 firstStart = first.anchoredPosition;
            Vector3 firstScaleStart = first.localScale;
            Vector2 secondStart = second != null ? second.anchoredPosition : Vector2.zero;
            Vector3 secondScaleStart = second != null ? second.localScale : Vector3.one;
            float elapsed = 0f;
            float duration = Mathf.Max(0.01f, animationDuration);

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float progress = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
                first.anchoredPosition = Vector2.LerpUnclamped(firstStart, firstTarget, progress);
                first.localScale = Vector3.LerpUnclamped(firstScaleStart, firstScaleTarget, progress);

                if (second != null)
                {
                    second.anchoredPosition = Vector2.LerpUnclamped(secondStart, secondTarget, progress);
                    second.localScale = Vector3.LerpUnclamped(secondScaleStart, secondScaleTarget, progress);
                }

                yield return null;
            }

            first.anchoredPosition = firstTarget;
            first.localScale = firstScaleTarget;
            if (second != null)
            {
                second.anchoredPosition = secondTarget;
                second.localScale = secondScaleTarget;
            }
        }

        void RefreshCards()
        {
            topCard.SetData(CurrentRecipe);
            topCard.gameObject.SetActive(CurrentRecipe != null);
            nextCard.SetData(NextRecipe);
            nextCard.gameObject.SetActive(NextRecipe != null);
        }

        void PositionCard(RecipeCardUI card, Vector2 position)
        {
            RectTransform cardRect = (RectTransform)card.transform;
            cardRect.anchoredPosition = position;
            cardRect.localScale = Vector3.one * cardScale;
            // A card that was faded out after completion comes back visible when reused.
            CardGroup(card).alpha = 1f;
        }

        Vector2 TopSlotPosition => new(-cardSpacing * 0.5f, 0f);
        Vector2 NextSlotPosition => new(cardSpacing * 0.5f, 0f);
    }
}
