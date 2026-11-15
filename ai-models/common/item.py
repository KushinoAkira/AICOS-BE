"""Menu item shape shared by the sample catalog, live Menu API, and the FE snapshot."""
from __future__ import annotations

from dataclasses import dataclass, field


@dataclass(frozen=True)
class Item:
    id: str
    name: str
    category: str
    price: int
    description: str
    sweetness: int  # 0 = none ... 3 = very sweet
    caffeine: bool
    dairy: bool
    vegan: bool
    hot: bool
    cold: bool
    tags: tuple[str, ...] = field(default_factory=tuple)
    allergens: tuple[str, ...] = field(default_factory=tuple)


def item(
    id: str,
    name: str,
    category: str,
    price: int,
    description: str,
    sweetness: int,
    caffeine: bool,
    dairy: bool,
    vegan: bool,
    hot: bool,
    cold: bool,
    tags: tuple[str, ...] = (),
    allergens: tuple[str, ...] = (),
) -> Item:
    return Item(id, name, category, price, description, sweetness, caffeine, dairy, vegan, hot, cold, tags, allergens)
