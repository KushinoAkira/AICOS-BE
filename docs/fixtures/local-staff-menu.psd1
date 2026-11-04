@{
    TenantCode = 'aicos-cashier-local'
    Categories = @('Cà phê', 'Trà & Trà sữa', 'Đồ uống khác', 'Đồ ăn', 'Bánh ngọt', 'Combo')
    Products = @(
        @{ Category='Cà phê'; Name='Cà phê đen đá'; Price=25000; Description='Cà phê phin đậm vị dùng với đá.'; Kind='drink'; Preset='coffee' }
        @{ Category='Cà phê'; Name='Cà phê sữa đá'; Price=29000; Description='Cà phê phin kết hợp sữa đặc.'; Kind='drink'; Preset='coffee' }
        @{ Category='Cà phê'; Name='Bạc xỉu'; Price=32000; Description='Sữa thơm béo cùng một chút cà phê.'; Kind='drink'; Preset='coffee' }
        @{ Category='Cà phê'; Name='Espresso'; Price=30000; Description='Một phần espresso nóng, vị cà phê nguyên bản.'; Kind='drink'; Preset='none' }
        @{ Category='Cà phê'; Name='Latte'; Price=39000; Description='Espresso cùng sữa tươi, dùng đá.'; Kind='drink'; Preset='coffee' }
        @{ Category='Cà phê'; Name='Cappuccino'; Price=39000; Description='Espresso, sữa nóng và lớp bọt sữa mịn.'; Kind='drink'; Preset='none' }
        @{ Category='Trà & Trà sữa'; Name='Trà đào cam sả'; Price=39000; Description='Trà đào thơm cùng cam và sả.'; Kind='drink'; Preset='tea' }
        @{ Category='Trà & Trà sữa'; Name='Trà vải'; Price=35000; Description='Trà trái cây vị vải thanh mát.'; Kind='drink'; Preset='tea' }
        @{ Category='Trà & Trà sữa'; Name='Trà tắc'; Price=25000; Description='Trà tắc chua ngọt, dùng đá.'; Kind='drink'; Preset='tea' }
        @{ Category='Trà & Trà sữa'; Name='Trà sen vàng'; Price=39000; Description='Trà sen cùng hạt sen và lớp kem sữa.'; Kind='drink'; Preset='tea' }
        @{ Category='Trà & Trà sữa'; Name='Trà sữa truyền thống'; Price=35000; Description='Trà sữa đậm vị, có thể chọn topping.'; Kind='drink'; Preset='tea' }
        @{ Category='Trà & Trà sữa'; Name='Trà sữa ô long'; Price=39000; Description='Trà ô long rang kết hợp sữa thơm béo.'; Kind='drink'; Preset='tea' }
        @{ Category='Đồ uống khác'; Name='Matcha latte'; Price=39000; Description='Matcha cùng sữa tươi, dùng đá.'; Kind='drink'; Preset='drink' }
        @{ Category='Đồ uống khác'; Name='Chocolate đá'; Price=35000; Description='Chocolate và sữa, thơm vị cacao.'; Kind='drink'; Preset='drink' }
        @{ Category='Đồ uống khác'; Name='Nước cam'; Price=35000; Description='Nước cam tươi, dùng đá.'; Kind='drink'; Preset='fruit' }
        @{ Category='Đồ uống khác'; Name='Nước suối chai'; Price=12000; Description='Một chai nước suối 500 ml.'; Kind='drink'; Preset='none' }
        @{ Category='Đồ ăn'; Name='Bánh mì hotdog'; Price=29000; Description='Bánh mì mềm kẹp xúc xích và sốt.'; Kind='food'; Preset='food' }
        @{ Category='Đồ ăn'; Name='Bánh mì gà xé'; Price=35000; Description='Bánh mì kẹp gà xé, rau và sốt.'; Kind='food'; Preset='food' }
        @{ Category='Đồ ăn'; Name='Sandwich jambon phô mai'; Price=39000; Description='Sandwich kẹp jambon và phô mai.'; Kind='food'; Preset='food' }
        @{ Category='Đồ ăn'; Name='Bánh mì trứng'; Price=25000; Description='Bánh mì kẹp trứng và rau.'; Kind='food'; Preset='food' }
        @{ Category='Đồ ăn'; Name='Croissant bơ'; Price=29000; Description='Bánh sừng bò nhiều lớp, thơm bơ.'; Kind='food'; Preset='food' }
        @{ Category='Bánh ngọt'; Name='Tiramisu'; Price=39000; Description='Một phần bánh tiramisu vị cà phê.'; Kind='food'; Preset='none' }
        @{ Category='Bánh ngọt'; Name='Cheesecake'; Price=35000; Description='Một phần bánh phô mai mềm mịn.'; Kind='food'; Preset='none' }
        @{ Category='Bánh ngọt'; Name='Brownie chocolate'; Price=29000; Description='Một phần brownie chocolate đậm vị.'; Kind='food'; Preset='none' }
        @{ Category='Combo'; Name='Combo Bạc xỉu + Bánh mì hotdog'; Price=55000; Description='Gồm 1 bạc xỉu cỡ M và 1 bánh mì hotdog. Giá bán trọn bộ.'; Kind='combo'; Preset='combo' }
        @{ Category='Combo'; Name='Combo Cà phê sữa + Bánh mì trứng'; Price=49000; Description='Gồm 1 cà phê sữa đá cỡ M và 1 bánh mì trứng. Giá bán trọn bộ.'; Kind='combo'; Preset='combo' }
        @{ Category='Combo'; Name='Combo Trà đào + Croissant bơ'; Price=59000; Description='Gồm 1 trà đào cam sả cỡ M và 1 croissant bơ. Giá bán trọn bộ.'; Kind='combo'; Preset='combo' }
        @{ Category='Combo'; Name='Combo Latte + Tiramisu'; Price=69000; Description='Gồm 1 latte đá cỡ M và 1 tiramisu. Giá bán trọn bộ.'; Kind='combo'; Preset='combo' }
    )
}
