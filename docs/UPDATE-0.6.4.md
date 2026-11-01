# HearthLink 0.6.4

The old Favorites checkbox filtered out every paired computer when none had been favorited. It did not delete computers, but the empty view and generic Favorite / unfavorite button made adding a first favorite hard to discover.

The filter is now labeled Favorites only. Select a computer and choose ☆ Add to favorites. For a favorite, the action becomes ★ Remove from favorites. The button is disabled when no computer is selected, and feedback confirms each change. Favorites are saved in the current Windows user's protected profile.

An empty filter shows a specific explanation and a Show all computers button. This clears both the Favorites filter and search, restoring the paired list. Removing the last favorite keeps paired computers and offers the same recovery.

Regression checks cover empty-filter recovery, selection-based add/remove, re-adding after removing the last favorite, saved profile persistence and stable computer identities when the list is reordered.
